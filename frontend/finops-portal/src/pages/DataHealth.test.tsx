import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { governanceApi } from '../services/governanceApi';
import { dataHealth } from '../test/fixtures';
import type { DataHealthOverviewResponse, DataHealthOverviewRow } from '../types/governance';
import DataHealth from './DataHealth';

vi.mock('../services/governanceApi', () => ({
  governanceApi: { getDataHealthOverview: vi.fn(), getDataHealth: vi.fn() },
}));

function row(overrides: Partial<DataHealthOverviewRow>): DataHealthOverviewRow {
  return { subscriptionId: '11111111-1111-1111-1111-111111111111', subscriptionName: 'Sub A', exportIngestion: true,
    publicationMode: 'Cutover', status: 'covered', coveragePercent: 100, missingDays: 0, latestUsageDate: '2026-10-13',
    isStale: false, lastImportedAt: '2026-10-14T06:00:00Z', lastPublicationStatus: 'Completed', ...overrides };
}

function overview(rows: DataHealthOverviewRow[], warnings: string[] = []): DataHealthOverviewResponse {
  return { from: '2026-09-30', to: '2026-10-13', checkedAt: '2026-10-15T12:00:00Z',
    subscriptionSource: 'cost-anomaly-report:2026-10-14', subscriptions: rows, warnings };
}

beforeEach(() => {
  vi.mocked(governanceApi.getDataHealthOverview).mockReset();
  vi.mocked(governanceApi.getDataHealth).mockReset();
});

describe('saúde dos dados (visão admin)', () => {
  it('lista todas as subscriptions automaticamente, sem pedir GUID', async () => {
    vi.mocked(governanceApi.getDataHealthOverview).mockResolvedValue(overview([
      row({ subscriptionId: '22222222-2222-2222-2222-222222222222', subscriptionName: 'Sub B', status: 'incomplete',
        coveragePercent: 50, missingDays: 7, isStale: true, latestUsageDate: '2026-10-06' }),
      row({}),
    ]));
    render(<DataHealth />);

    const subB = await screen.findByRole('row', { name: /Sub B/ });
    expect(within(subB).getByText('50%')).toBeInTheDocument();
    expect(within(subB).getByText('2026-10-06 (defasado)')).toBeInTheDocument();
    expect(within(screen.getByRole('row', { name: /Sub A/ })).getByText('100%')).toBeInTheDocument();
    expect(screen.queryByLabelText('Subscription ID')).not.toBeInTheDocument();
  });

  it('não mostra cobertura zero quando não há índice', async () => {
    vi.mocked(governanceApi.getDataHealthOverview).mockResolvedValue(overview([
      row({ status: 'unknown', coveragePercent: null, missingDays: null, latestUsageDate: null, lastImportedAt: null }),
    ]));
    render(<DataHealth />);

    const subA = await screen.findByRole('row', { name: /Sub A/ });
    expect(within(subA).getByText('Sem dados de export')).toBeInTheDocument();
    expect(within(subA).queryByText('0%')).not.toBeInTheDocument();
  });

  it('filtra por status e carrega os dias faltantes sob demanda', async () => {
    vi.mocked(governanceApi.getDataHealthOverview).mockResolvedValue(overview([
      row({ subscriptionId: '22222222-2222-2222-2222-222222222222', subscriptionName: 'Sub B', status: 'incomplete', missingDays: 1 }),
      row({}),
    ]));
    vi.mocked(governanceApi.getDataHealth).mockResolvedValue({ ...dataHealth(), missingDates: ['2026-10-02'] });
    const user = userEvent.setup();
    render(<DataHealth />);

    await user.click(await screen.findByRole('button', { name: 'Incompleta/defasada: 1' }));
    expect(screen.queryByRole('row', { name: /Sub A/ })).not.toBeInTheDocument();

    await user.click(within(screen.getByRole('row', { name: /Sub B/ })).getByRole('button', { name: 'Ver dias' }));
    expect(await screen.findByText('Dias sem dados: 2026-10-02')).toBeInTheDocument();
    expect(governanceApi.getDataHealth).toHaveBeenCalledWith('22222222-2222-2222-2222-222222222222', '2026-09-30', '2026-10-13');
  });

  it('mostra erro do backend sem inventar dados', async () => {
    vi.mocked(governanceApi.getDataHealthOverview).mockRejectedValue(new Error('Acesso negado'));
    render(<DataHealth />);
    expect(await screen.findByRole('alert')).toHaveTextContent('Acesso negado');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});
