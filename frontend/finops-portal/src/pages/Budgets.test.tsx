import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AccessProvider } from '../auth/AccessContext';
import { governanceApi } from '../services/governanceApi';
import { admin, budget, budgetEvaluation, deferred, reader } from '../test/fixtures';
import type { GovernanceBudget } from '../types/governance';
import Budgets from './Budgets';

vi.mock('../services/governanceApi', () => ({ governanceApi: {
  listBudgets: vi.fn(), evaluateBudget: vi.fn(), saveBudget: vi.fn(), deleteBudget: vi.fn(),
} }));

beforeEach(() => {
  vi.mocked(governanceApi.listBudgets).mockReset().mockResolvedValue([budget()]);
  vi.mocked(governanceApi.evaluateBudget).mockReset().mockResolvedValue(budgetEvaluation());
  vi.mocked(governanceApi.saveBudget).mockReset().mockResolvedValue(budget());
  vi.mocked(governanceApi.deleteBudget).mockReset().mockResolvedValue({ deleted: true });
});

describe('orçamentos e previsão', () => {
  it('permite leitura e avaliação sem oferecer mutações a Reader', async () => {
    const user = userEvent.setup(); render(<AccessProvider value={reader}><Budgets /></AccessProvider>);
    await user.click(await screen.findByRole('button', { name: 'Avaliar' }));
    expect(await screen.findByText('Estouro estimado')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Salvar' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Excluir' })).not.toBeInTheDocument();
    expect(governanceApi.saveBudget).not.toHaveBeenCalled();
  });

  it('identifica uma projeção baseada em dados incompletos', async () => {
    const user = userEvent.setup(); render(<AccessProvider value={reader}><Budgets /></AccessProvider>);
    await user.click(await screen.findByRole('button', { name: 'Avaliar' }));
    expect(await screen.findByText(/4\/8 dias observados\/esperados/)).toBeInTheDocument();
    expect(screen.getByText(/Não confiável: ver cobertura/)).toBeInTheDocument();
    expect(screen.getByText(/Dias sem custo validado.*2026-09-02/)).toBeInTheDocument();
    expect(screen.getByText(/não há dispatch, email, webhook ou alerta Azure/)).toBeInTheDocument();
  });

  it('não apresenta erro de carregamento como uma lista vazia', async () => {
    vi.mocked(governanceApi.listBudgets).mockRejectedValue(new Error('Serviço indisponível'));
    render(<AccessProvider value={reader}><Budgets /></AccessProvider>);
    expect(await screen.findByRole('alert')).toHaveTextContent('Serviço indisponível');
    expect(screen.queryByText('Nenhum orçamento cadastrado.')).not.toBeInTheDocument();
  });

  it('não transforma custos ausentes em zero', async () => {
    vi.mocked(governanceApi.evaluateBudget).mockResolvedValue({ ...budgetEvaluation(), observedCost: null,
      consumptionPercent: null, estimatedMonthEnd: null, forecastExceedsBudget: null });
    const user = userEvent.setup(); render(<AccessProvider value={reader}><Budgets /></AccessProvider>);
    await user.click(await screen.findByRole('button', { name: 'Avaliar' }));
    await screen.findByText('Sem percentual disponível');
    expect(screen.getAllByText('Indisponível')).toHaveLength(3);
    expect(screen.queryByText('Estouro estimado')).not.toBeInTheDocument();
  });

  it('envia a versão do orçamento ao editar e impede envio duplicado enquanto salva', async () => {
    const pending = deferred<GovernanceBudget>();
    vi.mocked(governanceApi.saveBudget).mockReturnValue(pending.promise);
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const user = userEvent.setup(); render(<AccessProvider value={admin}><Budgets /></AccessProvider>);
    await user.click(await screen.findByRole('button', { name: 'Editar' }));
    await user.clear(screen.getByLabelText('Limite'));
    await user.type(screen.getByLabelText('Limite'), '2500');
    await user.dblClick(screen.getByRole('button', { name: 'Salvar' }));
    expect(governanceApi.saveBudget).toHaveBeenCalledTimes(1);
    expect(governanceApi.saveBudget).toHaveBeenCalledWith('budget-1', expect.objectContaining({ amount: 2500, version: '"budget-etag"', costCenter: null }));
    expect(screen.getByRole('button', { name: 'Salvando…' })).toBeDisabled();
    pending.resolve(budget());
    await screen.findByText(/Orçamento salvo\./);
  });

  it('exclusão exige confirmação e mantém a versão para concorrência', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    const user = userEvent.setup(); render(<AccessProvider value={admin}><Budgets /></AccessProvider>);
    const remove = await screen.findByRole('button', { name: 'Excluir' });
    await user.click(remove);
    expect(governanceApi.deleteBudget).not.toHaveBeenCalled();
    await user.click(remove);
    expect(confirm).toHaveBeenCalledTimes(2);
    expect(governanceApi.deleteBudget).toHaveBeenCalledWith(budget());
    await screen.findByText('Orçamento excluído.');
  });

  it('um conflito de edição é exibido sem repetir a escrita', async () => {
    vi.mocked(governanceApi.saveBudget).mockRejectedValue(new Error('Versão desatualizada (409)'));
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const user = userEvent.setup(); render(<AccessProvider value={admin}><Budgets /></AccessProvider>);
    await user.click(await screen.findByRole('button', { name: 'Editar' }));
    await user.click(screen.getByRole('button', { name: 'Salvar' }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Versão desatualizada (409)'));
    expect(screen.getByLabelText('Nome')).toHaveValue('DEV mensal');
    expect(governanceApi.saveBudget).toHaveBeenCalledTimes(1);
  });
});
