import { beforeEach, describe, expect, it, vi } from 'vitest';
import { InteractionRequiredAuthError, type AuthenticationResult } from '@azure/msal-browser';
import { msalInstance } from '../auth/msalInstance';
import { ArmError, createResourceGroup, deleteEmptyResourceGroup, getProviderState, listResources, listSubscriptions, pollOperation, registerProvider, startMove } from './armApi';

vi.mock('../auth/msalInstance', () => ({ msalInstance: {
  getActiveAccount: vi.fn(), getAllAccounts: vi.fn(), loginRedirect: vi.fn(),
  acquireTokenSilent: vi.fn(), acquireTokenRedirect: vi.fn(),
} }));

const account = { homeAccountId: 'local', localAccountId: 'local', environment: 'login.microsoftonline.com', tenantId: 'tenant', username: 'admin@example.test' };
const ARM_SCOPE = { scopes: ['https://management.azure.com/user_impersonation'] };
const accepted = (location: string) => new Response(null, { status: 202, headers: { Location: location, 'Retry-After': '20' } });

beforeEach(() => {
  vi.mocked(msalInstance.getActiveAccount).mockReset().mockReturnValue(account);
  vi.mocked(msalInstance.getAllAccounts).mockReset().mockReturnValue([]);
  vi.mocked(msalInstance.acquireTokenRedirect).mockReset().mockResolvedValue();
  vi.mocked(msalInstance.acquireTokenSilent).mockReset().mockResolvedValue({ accessToken: 'arm-token' } as AuthenticationResult);
});

describe('cliente ARM das migrações', () => {
  it('usa o token ARM do usuário e segue a paginação', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(Response.json({ value: [{ subscriptionId: 'b', displayName: 'Beta', state: 'Enabled' }],
        nextLink: 'https://management.azure.com/subscriptions?page=2' }))
      .mockResolvedValueOnce(Response.json({ value: [{ subscriptionId: 'a', displayName: 'Alfa', state: 'Enabled' },
        { subscriptionId: 'x', displayName: 'Desabilitada', state: 'Disabled' }] }));
    await expect(listSubscriptions()).resolves.toEqual([{ subscriptionId: 'a', displayName: 'Alfa' }, { subscriptionId: 'b', displayName: 'Beta' }]);
    expect(msalInstance.acquireTokenSilent).toHaveBeenCalledWith({ ...ARM_SCOPE, account });
    expect(fetch).toHaveBeenLastCalledWith('https://management.azure.com/subscriptions?page=2',
      expect.objectContaining({ headers: expect.objectContaining({ Authorization: 'Bearer arm-token' }) }));
  });

  it('não segue nextLink que sai da listagem solicitada', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(Response.json({ value: [{ subscriptionId: 'a', displayName: 'Alfa' }],
      nextLink: 'https://management.azure.com/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01' }));
    await expect(listSubscriptions()).rejects.toThrow(/fora da listagem/);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('pede consentimento quando o ARM ainda não foi autorizado', async () => {
    vi.mocked(msalInstance.acquireTokenSilent).mockRejectedValue(new InteractionRequiredAuthError('consent_required'));
    await expect(listSubscriptions()).rejects.toBeInstanceOf(InteractionRequiredAuthError);
    expect(msalInstance.acquireTokenRedirect).toHaveBeenCalledWith({ ...ARM_SCOPE, account });
    expect(fetch).not.toHaveBeenCalled();
  });

  it('lista só recursos de primeiro nível (filhos como databases e webhooks recusam o move)', async () => {
    const rg = '/subscriptions/s/resourceGroups/rg-a/providers';
    vi.mocked(fetch).mockResolvedValueOnce(Response.json({ value: [
      { id: `${rg}/Microsoft.Sql/servers/sql1`, name: 'sql1', type: 'Microsoft.Sql/servers', location: 'eastus2' },
      { id: `${rg}/Microsoft.Sql/servers/sql1/databases/master`, name: 'sql1/master', type: 'Microsoft.Sql/servers/databases', location: 'eastus2' },
      { id: `${rg}/Microsoft.Sql/servers/sql1/databases/app`, name: 'sql1/app', type: 'Microsoft.Sql/servers/databases', location: 'eastus2' },
      { id: `${rg}/Microsoft.ContainerRegistry/registries/acr1/webhooks/wh1`, name: 'acr1/wh1', type: 'Microsoft.ContainerRegistry/registries/webhooks', location: 'eastus2' },
      { id: `${rg}/Microsoft.ContainerRegistry/registries/acr1`, name: 'acr1', type: 'Microsoft.ContainerRegistry/registries', location: 'eastus2' },
    ] }));
    const resources = await listResources('s', 'rg-a');
    expect(resources.map(r => r.name)).toEqual(['acr1', 'sql1']);
  });

  it('inicia o move com o corpo do ARM e devolve o Location', async () => {
    vi.mocked(fetch).mockResolvedValue(accepted('https://management.azure.com/subscriptions/s/operationresults/op'));
    const op = await startMove('move', 's', 'rg-a', ['/subscriptions/s/resourceGroups/rg-a/providers/X/y/z'], '/subscriptions/t/resourceGroups/rg-b');
    expect(op).toEqual({ location: 'https://management.azure.com/subscriptions/s/operationresults/op', retryAfterSeconds: 20 });
    expect(fetch).toHaveBeenCalledWith('https://management.azure.com/subscriptions/s/resourceGroups/rg-a/moveResources?api-version=2021-04-01',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({
        resources: ['/subscriptions/s/resourceGroups/rg-a/providers/X/y/z'], targetResourceGroup: '/subscriptions/t/resourceGroups/rg-b' }) }));
  });

  it('traz a mensagem e os detalhes do erro do ARM', async () => {
    vi.mocked(fetch).mockResolvedValue(Response.json({ error: { code: 'ResourceMoveFailed', message: 'Move inválido.',
      details: [{ code: 'MissingMoveDependentResources', message: 'Falta o ip-1.' }] } }, { status: 409 }));
    const result = await pollOperation({ location: 'https://management.azure.com/op', retryAfterSeconds: 5 });
    expect(result).toEqual({ state: 'failed', message: 'Move inválido.', details: ['MissingMoveDependentResources: Falta o ip-1.'] });
  });

  it('mostra os recursos dependentes ausentes que vêm em details aninhados', async () => {
    const identity = '/subscriptions/s/resourceGroups/redis-dev-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/redis-dev-identity';
    vi.mocked(fetch).mockResolvedValue(Response.json({ error: { code: 'ResourceMoveProviderValidationFailed', message: 'Falha na validação.',
      details: [{ code: 'MissingMoveDependentResources', message: 'A solicitação não contém todos os recursos dependentes.',
        details: [{ code: 'MissingResource', message: 'Recurso dependente ausente.', target: identity }] }] } }, { status: 409 }));
    const result = await pollOperation({ location: 'https://management.azure.com/op', retryAfterSeconds: 5 });
    expect(result).toEqual({ state: 'failed', message: 'Falha na validação.', details: [
      'MissingMoveDependentResources: A solicitação não contém todos os recursos dependentes.',
      `MissingResource: Recurso dependente ausente. (${identity})`] });
  });

  it('não acompanha operação fora do ARM', async () => {
    await expect(pollOperation({ location: 'https://exemplo.test/op', retryAfterSeconds: 5 })).rejects.toThrow(/fora do Azure Resource Manager/);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('cria o resource group com as tags informadas', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(new Response(null, { status: 404 }))
      .mockResolvedValueOnce(Response.json({ id: '/subscriptions/t/resourceGroups/rg-b', name: 'rg-b', location: 'eastus2', tags: { Setor: 'NAP' } }, { status: 201 }));
    await expect(createResourceGroup('t', 'rg-b', 'eastus2', { Setor: 'NAP' })).resolves.toMatchObject({ name: 'rg-b', tags: { Setor: 'NAP' } });
    expect(fetch).toHaveBeenLastCalledWith(expect.stringContaining('/subscriptions/t/resourcegroups/rg-b'),
      expect.objectContaining({ method: 'PUT', body: JSON.stringify({ location: 'eastus2', tags: { Setor: 'NAP' } }) }));
  });

  it('registra um resource provider e consulta o estado do registro', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(Response.json({ registrationState: 'Registering' }))
      .mockResolvedValueOnce(Response.json({ namespace: 'Microsoft.Web', registrationState: 'Registered' }));
    await registerProvider('s', 'Microsoft.Web');
    expect(fetch).toHaveBeenLastCalledWith('https://management.azure.com/subscriptions/s/providers/Microsoft.Web/register?api-version=2021-04-01',
      expect.objectContaining({ method: 'POST' }));
    await expect(getProviderState('s', 'Microsoft.Web')).resolves.toBe('Registered');
  });

  it('só exclui o resource group se estiver vazio, conferindo antes do DELETE', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(Response.json({ value: [{ id: 'x', name: 'disco', type: 'Microsoft.Compute/disks', location: 'eastus' }] }));
    await expect(deleteEmptyResourceGroup('s', 'rg-a')).rejects.toMatchObject({ code: 'ResourceGroupNotEmpty', details: ['disco (Microsoft.Compute/disks)'] });
    expect(fetch).toHaveBeenCalledTimes(1);

    vi.mocked(fetch).mockReset()
      .mockResolvedValueOnce(Response.json({ value: [] }))
      .mockResolvedValueOnce(accepted('https://management.azure.com/subscriptions/s/operationresults/del'));
    await expect(deleteEmptyResourceGroup('s', 'rg-a')).resolves.toEqual({ location: 'https://management.azure.com/subscriptions/s/operationresults/del', retryAfterSeconds: 20 });
    expect(fetch).toHaveBeenLastCalledWith('https://management.azure.com/subscriptions/s/resourcegroups/rg-a?api-version=2021-04-01',
      expect.objectContaining({ method: 'DELETE' }));
  });

  it('não exclui o resource group se o ARM listar só recursos filhos', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(Response.json({ value: [
      { id: 'x', name: 'sql1/app', type: 'Microsoft.Sql/servers/databases', location: 'eastus2' }] }));
    await expect(deleteEmptyResourceGroup('s', 'rg-a')).rejects.toMatchObject({ code: 'ResourceGroupNotEmpty' });
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('não reaproveita um resource group existente ao criar o destino', async () => {
    vi.mocked(fetch).mockResolvedValue(Response.json({ id: '/subscriptions/t/resourceGroups/rg-b', name: 'rg-b', location: 'eastus2' }));
    await expect(createResourceGroup('t', 'rg-b', 'eastus2')).rejects.toBeInstanceOf(ArmError);
    expect(fetch).toHaveBeenCalledTimes(1);
  });
});
