import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AccessProvider } from '../auth/AccessContext';
import * as arm from '../services/armApi';
import { admin, reader } from '../test/fixtures';
import Migrations from './Migrations';

vi.mock('../services/armApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../services/armApi')>();
  return {
    ArmError: actual.ArmError, portalResourceUrl: actual.portalResourceUrl,
    listSubscriptions: vi.fn(), listResourceGroups: vi.fn(), listResources: vi.fn(),
    createResourceGroup: vi.fn(), ensureResourceGroup: vi.fn(), startMove: vi.fn(), pollOperation: vi.fn(),
    registerProvider: vi.fn(), getProviderState: vi.fn(), deleteEmptyResourceGroup: vi.fn(),
  };
});

const SOURCE_RG = '/subscriptions/sub-a/resourceGroups/rg-origem';
const TARGET_RG = '/subscriptions/sub-b/resourceGroups/rg-destino';
const DISK = `${SOURCE_RG}/providers/Microsoft.Compute/disks/disco-1`;
const IP = `${SOURCE_RG}/providers/Microsoft.Network/publicIPAddresses/ip-1`;
const SECOND_RG = '/subscriptions/sub-a/resourceGroups/rg-segundo';
const VM = `${SECOND_RG}/providers/Microsoft.Compute/virtualMachines/vm-1`;
const operation = { location: 'https://management.azure.com/subscriptions/sub-a/operationresults/op', retryAfterSeconds: 0 };
type User = ReturnType<typeof userEvent.setup>;

beforeEach(() => {
  vi.mocked(arm.listSubscriptions).mockReset().mockResolvedValue([
    { subscriptionId: 'sub-a', displayName: 'MPN - Origem' }, { subscriptionId: 'sub-b', displayName: 'MPN - Destino' },
    { subscriptionId: 'sub-prod', displayName: 'Contoso' }]);
  vi.mocked(arm.listResourceGroups).mockReset().mockImplementation(async (sub) => sub === 'sub-a'
    ? [{ id: SOURCE_RG, name: 'rg-origem', location: 'eastus2', tags: { Setor: 'NAP', Responsável: 'a@b.test' } },
      { id: SECOND_RG, name: 'rg-segundo', location: 'eastus2', tags: { Setor: 'NAP' } },
      { id: '/subscriptions/sub-a/resourceGroups/rg-vazio', name: 'rg-vazio', location: 'eastus2' }]
    : [{ id: TARGET_RG, name: 'rg-destino', location: 'eastus2' }, { id: '/subscriptions/sub-b/resourceGroups/outro', name: 'outro', location: 'eastus2' }]);
  vi.mocked(arm.listResources).mockReset().mockImplementation(async (_sub, group) => group === 'rg-vazio' ? [] : group === 'rg-segundo'
    ? [{ id: VM, name: 'vm-1', type: 'Microsoft.Compute/virtualMachines', location: 'eastus2' }]
    : [{ id: DISK, name: 'disco-1', type: 'Microsoft.Compute/disks', location: 'eastus2' },
      { id: IP, name: 'ip-1', type: 'Microsoft.Network/publicIPAddresses', location: 'eastus2' }]);
  vi.mocked(arm.createResourceGroup).mockReset();
  vi.mocked(arm.ensureResourceGroup).mockReset().mockImplementation(async (sub, name, location) =>
    ({ id: `/subscriptions/${sub}/resourceGroups/${name}`, name, location }));
  vi.mocked(arm.startMove).mockReset().mockResolvedValue(operation);
  vi.mocked(arm.pollOperation).mockReset().mockResolvedValue({ state: 'succeeded' });
  vi.mocked(arm.registerProvider).mockReset().mockResolvedValue();
  vi.mocked(arm.getProviderState).mockReset().mockResolvedValue('Registered');
  vi.mocked(arm.deleteEmptyResourceGroup).mockReset().mockResolvedValue(null);
});

/** Seleciona uma opção do campo pesquisável: clica, (opcionalmente) digita e escolhe na lista. */
async function pick(user: User, field: string, option: string | RegExp, search?: string) {
  const box = screen.getByRole('combobox', { name: field });
  await waitFor(() => expect(box).toBeEnabled());
  await user.click(box);
  if (search) await user.type(box, search);
  await user.click(await screen.findByRole('option', { name: option }));
}

async function fillSelection(user: User, target: string | RegExp = /^rg-destino/) {
  render(<AccessProvider value={admin}><Migrations /></AccessProvider>);
  await pick(user, 'Subscription de origem', /^MPN - Origem/);
  await pick(user, 'Resource group de origem', /^rg-origem/);
  await user.click(await screen.findByLabelText('Mover ip-1'));
  await pick(user, 'Subscription de destino', /^MPN - Destino/);
  await pick(user, 'Resource group de destino', target);
}

describe('migração simples de recursos', () => {
  it('não consulta o Azure para Reader', () => {
    render(<AccessProvider value={reader}><Migrations /></AccessProvider>);
    expect(screen.getByRole('alert')).toHaveTextContent('Acesso exclusivo para FinOps.Admin.');
    expect(arm.listSubscriptions).not.toHaveBeenCalled();
  });

  it('só oferece subscriptions MPN, buscando pelo nome', async () => {
    const user = userEvent.setup();
    render(<AccessProvider value={admin}><Migrations /></AccessProvider>);
    const box = screen.getByRole('combobox', { name: 'Subscription de origem' });
    await waitFor(() => expect(box).toBeEnabled());
    await user.click(box);
    const options = () => within(screen.getByRole('listbox')).getAllByRole('option').map(o => o.textContent);
    expect(options()).toEqual(['MPN - Origemsub-a', 'MPN - Destinosub-b']);
    await user.type(box, 'dest');
    expect(options()).toEqual(['MPN - Destinosub-b']);
  });

  it('filtra os resource groups pelo nome e mantém "Criar novo" visível', async () => {
    const user = userEvent.setup();
    await fillSelection(user);
    const box = screen.getByRole('combobox', { name: 'Resource group de destino' });
    expect(box).toHaveValue('rg-destino');
    await user.click(box);
    await user.type(box, 'outr');
    const names = within(screen.getByRole('listbox')).getAllByRole('option').map(o => o.textContent);
    expect(names).toEqual(['+ Criar novo resource group', 'outroeastus2']);
  });

  it('valida, exige confirmação e move uma única vez', async () => {
    const user = userEvent.setup();
    await fillSelection(user);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    expect(await screen.findByText(/o Azure aceita mover 1 recurso/)).toBeInTheDocument();
    expect(arm.startMove).toHaveBeenCalledWith('validate', 'sub-a', 'rg-origem', [DISK], TARGET_RG);

    const move = screen.getByRole('button', { name: 'Mover 1 recurso(s)' });
    expect(move).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: /Entendo que 1 recurso/ }));
    await user.click(move);
    expect(await screen.findByText(/1 recurso\(s\) movido\(s\)/)).toBeInTheDocument();
    expect(arm.startMove).toHaveBeenCalledTimes(2);
    expect(arm.startMove).toHaveBeenLastCalledWith('move', 'sub-a', 'rg-origem', [DISK], TARGET_RG);
  });

  it('mostra o motivo da recusa do Azure e não oferece o move', async () => {
    vi.mocked(arm.pollOperation).mockResolvedValue({ state: 'failed', message: 'Recursos dependentes fora do pedido.',
      details: ['MissingMoveDependentResources: ip-1'] });
    const user = userEvent.setup();
    await fillSelection(user);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    expect(await screen.findByText('Recursos dependentes fora do pedido.')).toBeInTheDocument();
    expect(screen.getByText('MissingMoveDependentResources: ip-1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Mover/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Registrar/ })).not.toBeInTheDocument();
  });

  it('registra o provider que o Azure aponta como ausente, na subscription da mensagem', async () => {
    const subscription = '11111111-2222-3333-4444-555555555555';
    vi.mocked(arm.pollOperation).mockResolvedValueOnce({ state: 'failed', message: 'A solicitação tem 2 erros de validação.', details: [
      `MissingRegistrationsForTypes: A assinatura '${subscription}' não está registrada para os tipos de recurso 'Microsoft.Web/serverFarms (eastus),Microsoft.Web/sites (eastus)'.`] });
    const user = userEvent.setup();
    await fillSelection(user);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    await user.click(await screen.findByRole('button', { name: 'Registrar Microsoft.Web' }));
    expect(await screen.findByText(/Microsoft.Web registrado\(s\) na subscription. Valide novamente./)).toBeInTheDocument();
    expect(arm.registerProvider).toHaveBeenCalledWith(subscription, 'Microsoft.Web');
    expect(arm.registerProvider).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Validar' })).toBeEnabled();
  });

  it('alterar a seleção depois de validar exige nova validação', async () => {
    const user = userEvent.setup();
    await fillSelection(user);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    await screen.findByText(/o Azure aceita mover 1 recurso/);
    await user.click(screen.getByLabelText('Mover ip-1'));
    expect(screen.queryByRole('button', { name: /Mover \d/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Validar' })).toBeEnabled();
  });

  it('cria o RG de destino com o nome e as tags da origem antes de validar', async () => {
    const created = { id: '/subscriptions/sub-b/resourceGroups/rg-origem', name: 'rg-origem', location: 'eastus2' };
    vi.mocked(arm.createResourceGroup).mockResolvedValue(created);
    const user = userEvent.setup();
    await fillSelection(user, '+ Criar novo resource group');
    expect(screen.getByLabelText('Nome do novo resource group')).toHaveValue('rg-origem');
    await user.click(screen.getByRole('button', { name: 'Criar RG de destino e validar' }));
    expect(await screen.findByText(/o Azure aceita mover 1 recurso/)).toBeInTheDocument();
    expect(arm.createResourceGroup).toHaveBeenCalledWith('sub-b', 'rg-origem', 'eastus2', { Setor: 'NAP', Responsável: 'a@b.test' });
    expect(arm.startMove).toHaveBeenCalledWith('validate', 'sub-a', 'rg-origem', [DISK], created.id);
    expect(screen.getByRole('button', { name: 'Mover 1 recurso(s)' })).toBeInTheDocument();
  });

  async function validateAndMove(user: User) {
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    await screen.findByText(/o Azure aceita mover 1 recurso/);
    await user.click(screen.getByRole('checkbox', { name: /Entendo que 1 recurso/ }));
    await user.click(screen.getByRole('button', { name: 'Mover 1 recurso(s)' }));
    await screen.findByText(/1 recurso\(s\) movido\(s\)/);
  }

  it('depois do move, exclui o RG de origem vazio só com o nome digitado', async () => {
    const user = userEvent.setup();
    await fillSelection(user);
    vi.mocked(arm.listResources).mockResolvedValue([]); // origem vazia depois do move
    await validateAndMove(user);
    const remove = await screen.findByRole('button', { name: 'Excluir resource group de origem' });
    expect(remove).toBeDisabled();
    await user.type(screen.getByLabelText('Digite o nome do resource group para confirmar'), 'rg-orige');
    expect(remove).toBeDisabled();
    await user.type(screen.getByLabelText('Digite o nome do resource group para confirmar'), 'm');
    await user.click(remove);
    expect(await screen.findByText('rg-origem foi excluído.')).toBeInTheDocument();
    expect(arm.deleteEmptyResourceGroup).toHaveBeenCalledWith('sub-a', 'rg-origem');
  });

  it('acompanha a exclusão assíncrona (202) até concluir', async () => {
    const user = userEvent.setup();
    await fillSelection(user);
    vi.mocked(arm.listResources).mockResolvedValue([]);
    await validateAndMove(user);
    const deletion = { location: 'https://management.azure.com/subscriptions/sub-a/operationresults/del', retryAfterSeconds: 0 };
    vi.mocked(arm.deleteEmptyResourceGroup).mockResolvedValue(deletion);
    vi.mocked(arm.pollOperation).mockReset()
      .mockResolvedValueOnce({ state: 'running', retryAfterSeconds: 0 })
      .mockResolvedValueOnce({ state: 'succeeded' });
    await user.type(await screen.findByLabelText('Digite o nome do resource group para confirmar'), 'rg-origem');
    await user.click(screen.getByRole('button', { name: 'Excluir resource group de origem' }));
    expect(await screen.findByText('rg-origem foi excluído.')).toBeInTheDocument();
    expect(arm.pollOperation).toHaveBeenCalledTimes(2);
    expect(arm.pollOperation).toHaveBeenLastCalledWith(deletion);
  });

  it('não oferece excluir o RG de origem que ainda tem recursos', async () => {
    const user = userEvent.setup();
    await fillSelection(user);
    await validateAndMove(user); // listResources continua devolvendo 2 recursos
    expect(await screen.findByText(/rg-origem ainda tem 2 recurso\(s\)/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Excluir resource group de origem' })).not.toBeInTheDocument();
    expect(arm.deleteEmptyResourceGroup).not.toHaveBeenCalled();
  });

  async function fillBatch(user: User) {
    render(<AccessProvider value={admin}><Migrations /></AccessProvider>);
    await pick(user, 'Subscription de origem', /^MPN - Origem/);
    await pick(user, 'Resource group de origem', /^rg-origem/);
    await pick(user, 'Resource group de origem', /^rg-segundo/);
    await screen.findByLabelText('Mover vm-1');
    await pick(user, 'Subscription de destino', /^MPN - Destino/);
  }

  it('lote: valida todos os RGs e move em fila, cada um para o RG de mesmo nome', async () => {
    const user = userEvent.setup();
    await fillBatch(user);
    expect(screen.queryByRole('combobox', { name: 'Resource group de destino' })).not.toBeInTheDocument();
    expect(screen.getByText(/rg-origem → rg-origem/)).toHaveTextContent('(será criado)');
    expect(screen.getByLabelText('Mover ip-1')).toBeChecked(); // todos marcados por padrão

    await user.click(screen.getByRole('button', { name: 'Validar' }));
    expect(await screen.findByText(/o Azure aceita mover 3 recurso\(s\) de 2 resource groups/)).toBeInTheDocument();
    expect(arm.ensureResourceGroup).toHaveBeenCalledWith('sub-b', 'rg-origem', 'eastus2', { Setor: 'NAP', Responsável: 'a@b.test' });
    expect(arm.ensureResourceGroup).toHaveBeenCalledWith('sub-b', 'rg-segundo', 'eastus2', { Setor: 'NAP' });
    expect(arm.startMove).toHaveBeenCalledWith('validate', 'sub-a', 'rg-segundo', [VM], '/subscriptions/sub-b/resourceGroups/rg-segundo');

    await user.click(screen.getByRole('checkbox', { name: /Entendo que 3 recurso/ }));
    await user.click(screen.getByRole('button', { name: 'Mover 3 recurso(s)' }));
    expect(await screen.findByText(/3 recurso\(s\) movido\(s\)/)).toBeInTheDocument();
    const moves = vi.mocked(arm.startMove).mock.calls.filter(call => call[0] === 'move');
    expect(moves.map(call => call[2])).toEqual(['rg-origem', 'rg-segundo']);
    expect(moves[0][3]).toEqual([DISK, IP]);
  });

  it('lote: se um RG não validar, nada é movido e cada RG mostra o motivo', async () => {
    vi.mocked(arm.pollOperation).mockResolvedValueOnce({ state: 'succeeded' })
      .mockResolvedValueOnce({ state: 'failed', message: 'Tipo não suporta move.', details: [] });
    const user = userEvent.setup();
    await fillBatch(user);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    const alert = await screen.findByText(/1 resource group\(s\) com problema; nada será movido até todos validarem/);
    expect(alert).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'rg-segundo' })).toBeInTheDocument();
    expect(screen.getByText('Tipo não suporta move.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Mover/ })).not.toBeInTheDocument();
    expect(vi.mocked(arm.startMove).mock.calls.some(call => call[0] === 'move')).toBe(false);
  });

  it('adiciona todos os RGs da subscription; RG sem recursos fica fora do lote', async () => {
    const user = userEvent.setup();
    render(<AccessProvider value={admin}><Migrations /></AccessProvider>);
    await pick(user, 'Subscription de origem', /^MPN - Origem/);
    await user.click(await screen.findByRole('button', { name: 'Adicionar todos os resource groups (3)' }));
    const lote = screen.getByRole('list', { name: 'Resource groups de origem' });
    expect(within(lote).getAllByRole('listitem').map(li => li.textContent)).toEqual(['rg-origem', 'rg-segundo', 'rg-vazio']);
    expect(await screen.findByText(/Fora do lote \(nenhum recurso marcado\): rg-vazio/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Adicionar todos os resource groups (0)' })).toBeDisabled();

    await pick(user, 'Subscription de destino', /^MPN - Destino/);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    expect(await screen.findByText(/o Azure aceita mover 3 recurso\(s\) de 2 resource groups/)).toBeInTheDocument();
    expect(vi.mocked(arm.startMove).mock.calls.map(call => call[2])).toEqual(['rg-origem', 'rg-segundo']);
    expect(arm.ensureResourceGroup).not.toHaveBeenCalledWith('sub-b', 'rg-vazio', expect.anything(), expect.anything());
  });

  it('remove todos os RGs do lote de uma vez', async () => {
    const user = userEvent.setup();
    await fillBatch(user);
    await user.click(screen.getByRole('button', { name: 'Remover todos (2)' }));
    expect(screen.queryByRole('list', { name: 'Resource groups de origem' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Mover vm-1')).not.toBeInTheDocument();
  });

  it('lote: registra de uma vez todos os providers pendentes, inclusive os escritos em minúsculas', async () => {
    const sub = '11111111-2222-3333-4444-555555555555';
    const missing = (types: string) => `MissingRegistrationsForTypes: A assinatura '${sub}' não está registrada para os tipos de recurso '${types}'.`;
    vi.mocked(arm.pollOperation)
      .mockResolvedValueOnce({ state: 'failed', message: 'Erros de validação.', details: [
        missing('Microsoft.DocumentDB/databaseAccounts (eastus),Microsoft.Insights/components (eastus)'),
        missing('Microsoft.DocumentDB/databaseAccounts (eastus),Microsoft.Insights/components (eastus)')] })
      .mockResolvedValueOnce({ state: 'failed', message: 'Erros de validação.', details: [
        missing('microsoft.alertsmanagement/smartDetectorAlertRules (global),microsoft.insights/actiongroups (global)')] });
    const user = userEvent.setup();
    await fillBatch(user);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    // Linha repetida pelo Azure aparece uma vez só.
    expect(await screen.findAllByText(/databaseAccounts \(eastus\),Microsoft.Insights\/components/)).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Registrar todos (3)' }));
    expect(await screen.findByText(/registrado\(s\) na subscription. Valide novamente./)).toBeInTheDocument();
    expect(vi.mocked(arm.registerProvider).mock.calls).toEqual([
      [sub, 'Microsoft.DocumentDB'], [sub, 'Microsoft.Insights'], [sub, 'microsoft.alertsmanagement']]);
  });

  it('desmarca os recursos de tipo que o Azure não move e mantém a recusa visível', async () => {
    const waf = `${SOURCE_RG}/providers/Microsoft.Network/frontdoorWebApplicationFirewallPolicies/waf1`;
    vi.mocked(arm.listResources).mockImplementation(async () => [
      { id: DISK, name: 'disco-1', type: 'Microsoft.Compute/disks', location: 'eastus2' },
      { id: waf, name: 'waf1', type: 'Microsoft.Network/frontdoorWebApplicationFirewallPolicies', location: 'global' }]);
    vi.mocked(arm.pollOperation).mockResolvedValueOnce({ state: 'failed', message: 'Erros de validação.', details: [
      "ResourceMoveNotSupported: Não há suporte para a movimentação de recursos para os tipos de recursos 'Microsoft.Network/frontdoorWebApplicationFirewallPolicies'."] });
    const user = userEvent.setup();
    render(<AccessProvider value={admin}><Migrations /></AccessProvider>);
    await pick(user, 'Subscription de origem', /^MPN - Origem/);
    await pick(user, 'Resource group de origem', /^rg-origem/);
    await pick(user, 'Subscription de destino', /^MPN - Destino/);
    await pick(user, 'Resource group de destino', /^rg-destino/);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    await user.click(await screen.findByRole('button', { name: 'Desmarcar os recursos que não podem ser movidos' }));
    expect(screen.getByLabelText('Mover waf1')).not.toBeChecked();
    expect(screen.getByLabelText('Mover disco-1')).toBeChecked();
    expect(screen.getByText('A seleção mudou desde esta validação: valide novamente.')).toBeInTheDocument();
  });

  it('reconhece vários tipos não suportados na mesma mensagem do Azure', async () => {
    const identity = `${SOURCE_RG}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id1`;
    const alert = `${SOURCE_RG}/providers/microsoft.insights/metricalerts/alerta1`;
    vi.mocked(arm.listResources).mockImplementation(async () => [
      { id: DISK, name: 'disco-1', type: 'Microsoft.Compute/disks', location: 'eastus2' },
      { id: identity, name: 'id1', type: 'Microsoft.ManagedIdentity/userAssignedIdentities', location: 'eastus2' },
      { id: alert, name: 'alerta1', type: 'microsoft.insights/metricalerts', location: 'global' }]);
    vi.mocked(arm.pollOperation).mockResolvedValueOnce({ state: 'failed', message: 'Erros de validação.', details: [
      "ResourceMoveNotSupported: Não há suporte para a movimentação de recursos para os tipos de recursos 'Microsoft.ManagedIdentity/userAssignedIdentities, microsoft.insights/metricalerts'."] });
    const user = userEvent.setup();
    render(<AccessProvider value={admin}><Migrations /></AccessProvider>);
    await pick(user, 'Subscription de origem', /^MPN - Origem/);
    await pick(user, 'Resource group de origem', /^rg-origem/);
    await pick(user, 'Subscription de destino', /^MPN - Destino/);
    await pick(user, 'Resource group de destino', /^rg-destino/);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    await user.click(await screen.findByRole('button', { name: 'Desmarcar os recursos que não podem ser movidos' }));
    expect(screen.getByLabelText('Mover id1')).not.toBeChecked();
    expect(screen.getByLabelText('Mover alerta1')).not.toBeChecked();
    expect(screen.getByLabelText('Mover disco-1')).toBeChecked();
  });

  it('lote: remove um RG da lista e volta ao modo de um RG', async () => {
    const user = userEvent.setup();
    await fillBatch(user);
    await user.click(screen.getByRole('button', { name: 'Remover rg-segundo' }));
    expect(screen.queryByLabelText('Mover vm-1')).not.toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Resource group de destino' })).toBeInTheDocument();
  });

  it('se o acompanhamento falhar, consulta de novo sem reenviar o move', async () => {
    const user = userEvent.setup();
    await fillSelection(user);
    await user.click(screen.getByRole('button', { name: 'Validar' }));
    await screen.findByText(/o Azure aceita mover 1 recurso/);
    await user.click(screen.getByRole('checkbox', { name: /Entendo que 1 recurso/ }));
    vi.mocked(arm.pollOperation).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    await user.click(screen.getByRole('button', { name: 'Mover 1 recurso(s)' }));
    expect(await screen.findByText(/Não foi possível acompanhar o move/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Consultar o andamento' }));
    expect(await screen.findByText(/1 recurso\(s\) movido\(s\)/)).toBeInTheDocument();
    expect(arm.startMove).toHaveBeenCalledTimes(2); // validate + move; o acompanhamento nunca reenvia o POST
  });
});
