import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthenticationResult } from '@azure/msal-browser';
import { msalInstance } from '../auth/msalInstance';
import { apiTokenRequest } from '../auth/authConfig';
import { reader } from '../test/fixtures';
import { api, ApiError } from './api';

vi.mock('../auth/msalInstance', () => ({ msalInstance: {
  getActiveAccount: vi.fn(), getAllAccounts: vi.fn(), loginRedirect: vi.fn(),
  acquireTokenSilent: vi.fn(), acquireTokenRedirect: vi.fn(),
} }));
vi.mock('../auth/authConfig', () => ({ apiTokenRequest: { scopes: ['api://finops-test/FinOps.Access'] } }));

const account = { homeAccountId: 'local', localAccountId: 'local', environment: 'login.microsoftonline.com', tenantId: 'tenant', username: 'admin@example.test' };
beforeEach(() => {
  vi.mocked(msalInstance.getActiveAccount).mockReset().mockReturnValue(account);
  vi.mocked(msalInstance.getAllAccounts).mockReset().mockReturnValue([]);
  vi.mocked(msalInstance.loginRedirect).mockReset().mockResolvedValue();
  vi.mocked(msalInstance.acquireTokenRedirect).mockReset().mockResolvedValue();
  vi.mocked(msalInstance.acquireTokenSilent).mockReset().mockResolvedValue({ accessToken: 'test-access-token' } as AuthenticationResult);
  apiTokenRequest.scopes = ['api://finops-test/FinOps.Access'];
});

describe('autenticação e HTTP do portal', () => {
  it('solicita o scope da API e envia o access token Bearer', async () => {
    vi.mocked(fetch).mockResolvedValue(Response.json(reader));
    await expect(api.me()).resolves.toEqual(reader);
    expect(msalInstance.acquireTokenSilent).toHaveBeenCalledWith({ scopes: ['api://finops-test/FinOps.Access'], account });
    expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/me'), expect.objectContaining({
      headers: { Authorization: 'Bearer test-access-token', Accept: 'application/json' }, signal: expect.any(AbortSignal),
    }));
  });

  it('sem sessão inicia login e não chama a API', async () => {
    vi.mocked(msalInstance.getActiveAccount).mockReturnValue(null);
    await expect(api.me()).rejects.toThrow('Usuário não autenticado');
    expect(msalInstance.loginRedirect).toHaveBeenCalledTimes(1);
    expect(msalInstance.acquireTokenSilent).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('scope ausente bloqueia aquisição de token e HTTP', async () => {
    apiTokenRequest.scopes = [];
    await expect(api.me()).rejects.toThrow('VITE_FINOPS_API_SCOPE não configurado');
    expect(msalInstance.acquireTokenSilent).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('timeout de autenticação silenciosa solicita interação sem repetir o HTTP', async () => {
    const failure = { errorCode: 'monitor_window_timeout' };
    vi.mocked(msalInstance.acquireTokenSilent).mockRejectedValue(failure);
    await expect(api.me()).rejects.toBe(failure);
    expect(msalInstance.acquireTokenRedirect).toHaveBeenCalledWith({ scopes: apiTokenRequest.scopes, account });
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([401, 403, 503])('preserva status/código HTTP %i e só renova login em 401', async (status) => {
    vi.mocked(fetch).mockResolvedValue(Response.json({ error: { code: 'AuthNotConfigured' } }, { status }));
    const failure = api.me();
    await expect(failure).rejects.toBeInstanceOf(ApiError);
    await expect(failure).rejects.toMatchObject({ status, code: 'AuthNotConfigured' });
    expect(msalInstance.loginRedirect).toHaveBeenCalledTimes(status === 401 ? 1 : 0);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('aborta uma leitura após o limite de tempo e libera o timer', async () => {
    vi.useFakeTimers();
    vi.mocked(fetch).mockImplementation((_input, options) => new Promise((_resolve, reject) => {
      options!.signal!.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true });
    }));
    const assertion = expect(api.me()).rejects.toThrow('Tempo limite excedido ao chamar a API');
    await vi.advanceTimersByTimeAsync(90_000);
    await assertion;
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });
});
