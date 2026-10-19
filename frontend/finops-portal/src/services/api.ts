import type {
  BillingPeriodOverride,
  CostAnomaly,
  CostAnomalyRunResponse,
  CostAnomalyRunStatus,
  CostAllocationBreakdown,
  CostAllocationRule,
  CostAllocationRulesConfig,
  CostApiResponse,
  CostByResourceGroupRow,
  CostByResourceRow,
  CostByServiceRow,
  CostDatabaseOverview,
  CostTrendRow,
  CurrentUserInfo,
  DashboardDatabasesResponse,
  DashboardSummaryResponse,
  DashboardTeamsResponse,
  OwnershipConfig,
  OwnershipManagementGroupSyncResult,
  RecommendationDetailResponse,
  RecommendationHistoryEntry,
  RecommendationsSummary,
  TeamOwnership,
  TeamsResponse,
  SubscriptionOwnership,
  UpdateSubscriptionOwnershipPayload,
} from '../types/api';
import { BrowserAuthError, InteractionRequiredAuthError } from '@azure/msal-browser';
import { apiTokenRequest } from '../auth/authConfig';
import { msalInstance } from '../auth/msalInstance';
import { configureGovernanceApi, governanceApi } from './governanceApi';

function normalizeApiBaseUrl(value: string | undefined): string {
  const trimmed = (value ?? '').trim().replace(/\/+$/, '');
  return trimmed.endsWith('/api') ? trimmed.slice(0, -4) : trimmed;
}

const API_BASE_URL = normalizeApiBaseUrl(import.meta.env.VITE_API_BASE_URL);

function requiresInteractiveAuth(error: unknown): boolean {
  if (error instanceof InteractionRequiredAuthError) {
    return true;
  }

  if (error instanceof BrowserAuthError) {
    return error.errorCode === 'timed_out' || error.errorCode === 'monitor_window_timeout';
  }

  if (typeof error === 'object' && error !== null && 'errorCode' in error) {
    const errorCode = String((error as { errorCode?: unknown }).errorCode ?? '');
    return errorCode === 'timed_out' || errorCode === 'monitor_window_timeout';
  }

  return false;
}

async function getAccessToken(): Promise<string> {
  const account = msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0];

  if (!account) {
    await msalInstance.loginRedirect();
    throw new Error('Usuário não autenticado.');
  }

  if (!apiTokenRequest.scopes[0]) {
    throw new Error('VITE_FINOPS_API_SCOPE não configurado.');
  }

  let accessToken: string;
  try {
    const tokenResponse = await msalInstance.acquireTokenSilent({
      ...apiTokenRequest,
      account,
    });
    accessToken = tokenResponse.accessToken;
  } catch (error) {
    if (requiresInteractiveAuth(error)) {
      await msalInstance.acquireTokenRedirect({
        ...apiTokenRequest,
        account,
      });
    }
    throw error;
  }

  return accessToken;
}

async function parseErrorResponse(res: Response): Promise<{ message: string | null; code?: string }> {
  const contentType = res.headers.get('content-type') ?? '';

  if (contentType.includes('application/json')) {
    try {
      const payload = await res.json() as { message?: unknown; error?: unknown } | null;
      const error = payload?.error;
      const code = typeof error === 'object' && error !== null && 'code' in error && typeof error.code === 'string'
        ? error.code : undefined;
      return { message: typeof payload?.message === 'string' ? payload.message : typeof error === 'string' ? error : null, code };
    } catch {
      return { message: null };
    }
  }

  try {
    const text = await res.text();
    return { message: text || null };
  } catch {
    return { message: null };
  }
}

function mapStatusMessage(status: number, fallback: string | null): string {
  if (status === 401) {
    return '401: sessão expirada ou usuário não autenticado.';
  }

  if (status === 403) {
    return '403: acesso negado.';
  }

  if (status === 404) {
    return fallback ? `404: ${fallback}` : '404: recurso/configuração não encontrada.';
  }

  if (status === 504) {
    return '504: tempo limite excedido ao processar a solicitação.';
  }

  if (status >= 500) {
    return `${status}: erro inesperado ao processar a solicitação.`;
  }

  return `${status}: ${fallback ?? 'falha na chamada da API.'}`;
}

export class ApiError extends Error {
  status: number;
  code?: string;

  constructor(status: number, message: string, code?: string) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

function mapNetworkError(error: unknown): Error {
  if (error instanceof DOMException && error.name === 'AbortError') {
    return new Error('Tempo limite excedido ao chamar a API.');
  }

  return error instanceof Error ? error : new Error('Falha de rede ao chamar a API.');
}

async function fetchApi(path: string, accept: string, options?: { signal?: AbortSignal }): Promise<Response> {
  const accessToken = await getAccessToken();
  const controller = new AbortController();
  const abort = () => controller.abort();
  options?.signal?.addEventListener('abort', abort, { once: true });
  if (options?.signal?.aborted) controller.abort();
  const timeoutId = window.setTimeout(() => controller.abort(), 90_000);
  let res: Response;
  try {
    res = await fetch(`${API_BASE_URL}${path}`, {
      headers: {
        Authorization: `Bearer ${accessToken}`,
        Accept: accept,
      },
      signal: controller.signal,
    });
  } catch (error) {
    throw mapNetworkError(error);
  } finally {
    window.clearTimeout(timeoutId);
    options?.signal?.removeEventListener('abort', abort);
  }

  if (!res.ok) {
    const details = await parseErrorResponse(res);
    if (res.status === 401) {
      await msalInstance.loginRedirect().catch(() => undefined);
    }

    throw new ApiError(res.status, `${mapStatusMessage(res.status, details.message)}${details.code ? ` (${details.code})` : ''}`, details.code);
  }
  return res;
}

async function fetchJson<T>(path: string, options?: { signal?: AbortSignal }): Promise<T> {
  const res = await fetchApi(path, 'application/json', options);
  return res.json();
}

async function fetchText(path: string, accept = 'text/plain'): Promise<string> {
  const res = await fetchApi(path, accept);
  return res.text();
}

async function fetchBlob(path: string, accept: string): Promise<Blob> {
  const res = await fetchApi(path, accept);
  return res.blob();
}

async function sendJson<T>(
  path: string,
  method: 'POST' | 'PUT' | 'PATCH' | 'DELETE',
  body?: unknown,
  options?: { timeoutMs?: number; ifMatch?: string }
): Promise<T> {
  const accessToken = await getAccessToken();
  const controller = new AbortController();
  const timeoutId = window.setTimeout(() => controller.abort(), options?.timeoutMs ?? 90_000);
  let res: Response;
  try {
    res = await fetch(`${API_BASE_URL}${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${accessToken}`,
        Accept: 'application/json',
        ...(options?.ifMatch ? { 'If-Match': options.ifMatch } : {}),
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      ...(body ? { body: JSON.stringify(body) } : {}),
      signal: controller.signal,
    });
  } catch (error) {
    throw mapNetworkError(error);
  } finally {
    window.clearTimeout(timeoutId);
  }

  if (!res.ok) {
    const details = await parseErrorResponse(res);
    if (res.status === 401) {
      await msalInstance.loginRedirect().catch(() => undefined);
    }

    throw new ApiError(res.status, `${mapStatusMessage(res.status, details.message)}${details.code ? ` (${details.code})` : ''}`, details.code);
  }

  if (res.status === 204) {
    return {} as T;
  }

  return res.json();
}

function buildReportPath(format: 'html' | 'csv', date: string, subscriptionId?: string, team?: string): string {
  const params = new URLSearchParams();
  if (date) params.set('date', date);
  if (subscriptionId) params.set('subscription', subscriptionId);
  if (team) params.set('team', team);
  return `/api/report/${format}?${params}`;
}

configureGovernanceApi({ get: fetchJson, send: sendJson });

export const api = {
  ...governanceApi,
  me: () => fetchJson<CurrentUserInfo>('/api/me'),

  // Health
  health: () => fetchJson<string>('/api/health'),

  // Recommendations
  getRecommendations: (date: string, subscriptionId?: string) => {
    const params = new URLSearchParams({ date });
    if (subscriptionId) params.set('subscriptionId', subscriptionId);
    return fetchJson<RecommendationsSummary>(`/api/recommendations?${params}`);
  },

  updateRecommendationStatus: (id: string, payload: {
    status: string;
    reason?: string;
    snoozeUntil?: string;
    clearSnooze?: boolean;
    resourceId: string;
    subscriptionId: string;
    resourceGroup: string;
    teamId?: string | null;
    recommendationType: string;
  }) =>
    sendJson<{ status: string; updatedAt: string }>(
      `/api/recommendations/${encodeURIComponent(id)}/status`,
      'PATCH',
      payload
    ),

  getRecommendationDetail: (id: string, date?: string, historyDays?: number, snapshotRevision?: string | null) => {
    const params = new URLSearchParams();
    if (date) params.set('date', date);
    if (historyDays) params.set('historyDays', historyDays.toString());
    if (snapshotRevision) params.set('snapshotRevision', snapshotRevision);
    const suffix = params.toString();
    return fetchJson<RecommendationDetailResponse>(`/api/recommendations/${encodeURIComponent(id)}${suffix ? `?${suffix}` : ''}`);
  },

  getRecommendationHistory: (id: string, limit?: number) => {
    const params = new URLSearchParams();
    if (limit) params.set('limit', limit.toString());
    const suffix = params.toString();
    return fetchJson<{ recommendationId: string; total: number; items: RecommendationHistoryEntry[] }>(
      `/api/recommendations/${encodeURIComponent(id)}/history${suffix ? `?${suffix}` : ''}`
    );
  },

  // Dashboard summaries (Cosmos precomputed)
  getDashboardSummary: (date: string) => {
    const params = new URLSearchParams({ date });
    return fetchJson<DashboardSummaryResponse>(`/api/dashboard/summary?${params}`);
  },

  getDashboardTeams: (date: string) => {
    const params = new URLSearchParams({ date });
    return fetchJson<DashboardTeamsResponse>(`/api/dashboard/teams?${params}`);
  },

  getDashboardDatabases: (date: string) => {
    const params = new URLSearchParams({ date });
    return fetchJson<DashboardDatabasesResponse>(`/api/dashboard/databases?${params}`);
  },

  // Reports
  getReportHtmlUrl: (date: string, subscriptionId?: string, team?: string): string => {
    return `${API_BASE_URL}${buildReportPath('html', date, subscriptionId, team)}`;
  },

  getReportCsvUrl: (date: string, subscriptionId?: string, team?: string): string => {
    return `${API_BASE_URL}${buildReportPath('csv', date, subscriptionId, team)}`;
  },

  getReportHtml: (date: string, subscriptionId?: string, team?: string) => {
    return fetchText(buildReportPath('html', date, subscriptionId, team), 'text/html');
  },

  getReportCsv: (date: string, subscriptionId?: string, team?: string) => {
    return fetchBlob(buildReportPath('csv', date, subscriptionId, team), 'text/csv');
  },

  // Snapshots de custo (por serviço / por recurso)
  getCostTrendByService: (from: string, to: string, subscriptionId?: string) => {
    const params = new URLSearchParams({ from, to });
    if (subscriptionId) params.set('subscription', subscriptionId);
    return fetchJson<CostApiResponse<CostTrendRow>>(`/api/cost/trend-by-service?${params}`);
  },

  getCostByService: (date: string, subscriptionId?: string) => {
    const params = new URLSearchParams({ date });
    if (subscriptionId) params.set('subscription', subscriptionId);
    return fetchJson<CostApiResponse<CostByServiceRow>>(`/api/cost/by-service?${params}`);
  },

  getCostByResource: (date: string, options?: {
    subscriptionId?: string;
    service?: string;
    resource?: string;
    resourceGroup?: string;
    page?: number;
    pageSize?: number;
  }) => {
    const params = new URLSearchParams({ date });
    if (options?.subscriptionId) params.set('subscription', options.subscriptionId);
    if (options?.service) params.set('service', options.service);
    if (options?.resource) params.set('resource', options.resource);
    if (options?.resourceGroup) params.set('resourceGroup', options.resourceGroup);
    if (options?.page) params.set('page', options.page.toString());
    if (options?.pageSize) params.set('pageSize', options.pageSize.toString());
    return fetchJson<CostApiResponse<CostByResourceRow>>(`/api/cost/by-resource?${params}`);
  },

  // Custos agrupados por resource group: a grade abre com dezenas de linhas em vez de milhares e
  // busca os recursos de um grupo só quando alguém expande.
  getCostByResourceGroup: (date: string, options?: {
    subscriptionId?: string;
    service?: string;
    resourceGroup?: string;
    minCost?: number;
    page?: number;
    pageSize?: number;
  }) => {
    const params = new URLSearchParams({ date });
    if (options?.subscriptionId) params.set('subscription', options.subscriptionId);
    if (options?.service) params.set('service', options.service);
    if (options?.resourceGroup) params.set('resourceGroup', options.resourceGroup);
    if (options?.minCost) params.set('minCost', options.minCost.toString());
    if (options?.page) params.set('page', options.page.toString());
    if (options?.pageSize) params.set('pageSize', options.pageSize.toString());
    return fetchJson<CostApiResponse<CostByResourceGroupRow>>(`/api/cost/by-resource-group?${params}`);
  },

  // Rateio por centro de custo. Sem subscriptionId a API responde pela assinatura de produção;
  // 'all' consolida todas, de forma explícita.
  getCostAllocation: (from: string, to: string, subscriptionId?: string) => {
    const params = new URLSearchParams({ from, to });
    if (subscriptionId) params.set('subscription', subscriptionId);
    return fetchJson<CostAllocationBreakdown>(`/api/cost/allocation?${params}`);
  },

  getCostAllocationRules: () => fetchJson<CostAllocationRulesConfig>('/api/cost/allocation/rules'),

  createCostAllocationRule: (payload: {
    type: string;
    match: string;
    costCenter: string;
    subscriptionId?: string;
    enabled?: boolean;
    note?: string;
  }) => sendJson<CostAllocationRule>('/api/cost/allocation/rules', 'POST', payload),

  updateCostAllocationRule: (ruleId: string, payload: {
    type?: string;
    match?: string;
    costCenter?: string;
    subscriptionId?: string;
    enabled?: boolean;
    note?: string;
  }) => sendJson<CostAllocationRule>(`/api/cost/allocation/rules/${encodeURIComponent(ruleId)}`, 'PUT', payload),

  deleteCostAllocationRule: (ruleId: string) =>
    sendJson<{ message: string; ruleId: string }>(
      `/api/cost/allocation/rules/${encodeURIComponent(ruleId)}`,
      'DELETE'
    ),

  getCostDatabases: (from: string, to: string, subscriptionId?: string) => {
    const params = new URLSearchParams({ from, to });
    if (subscriptionId) params.set('subscription', subscriptionId);
    return fetchJson<CostDatabaseOverview>(`/api/cost/databases?${params}`);
  },

  // Cost Anomalies
  getAnomalies: (date?: string, days?: number, subscriptionId?: string, team?: string) => {
    const params = new URLSearchParams();
    if (date) params.set('date', date);
    if (days) params.set('days', days.toString());
    if (subscriptionId) params.set('subscriptionId', subscriptionId);
    if (team) params.set('team', team);
    return fetchJson<CostAnomaly[]>(`/api/cost-anomalies?${params}`);
  },

  runCostAnomalyAnalysis: () =>
    sendJson<CostAnomalyRunResponse>('/api/cost-anomalies/run', 'POST'),

  getCostAnomalyRunStatus: (runId: string) =>
    fetchJson<CostAnomalyRunStatus>(`/api/cost-anomalies/runs/${encodeURIComponent(runId)}`),

  getBillingPeriodOverrides: async () => {
    const payload = await fetchJson<{ count: number; overrides: BillingPeriodOverride[] }>('/api/billing/periods/overrides');
    return payload.overrides ?? [];
  },

  upsertBillingPeriodOverride: (subscriptionId: string, billingCycleStartDay: number) =>
    sendJson<BillingPeriodOverride>(
      `/api/billing/periods/overrides/${encodeURIComponent(subscriptionId)}`,
      'PUT',
      { billingCycleStartDay }
    ),

  deleteBillingPeriodOverride: (subscriptionId: string) =>
    sendJson<{ message: string; subscriptionId: string }>(
      `/api/billing/periods/overrides/${encodeURIComponent(subscriptionId)}`,
      'DELETE'
    ),

  // Teams
  getTeams: () => fetchJson<TeamsResponse>('/api/teams'),

  // Ownership management
  getOwnershipTeams: () => fetchJson<OwnershipConfig>('/api/ownership/teams'),

  getOwnershipTeam: (teamId: string) => fetchJson<TeamOwnership>(`/api/ownership/teams/${encodeURIComponent(teamId)}`),

  addOwnershipSubscription: (teamId: string, payload: SubscriptionOwnership) =>
    sendJson<{ message: string; teamId: string; subscription: SubscriptionOwnership }>(
      `/api/ownership/teams/${encodeURIComponent(teamId)}/subscriptions`,
      'POST',
      payload
    ),

  updateOwnershipSubscription: (teamId: string, subscriptionId: string, payload: UpdateSubscriptionOwnershipPayload) =>
    sendJson<{ message: string; teamId: string; subscription: SubscriptionOwnership }>(
      `/api/ownership/teams/${encodeURIComponent(teamId)}/subscriptions/${encodeURIComponent(subscriptionId)}`,
      'PUT',
      payload
    ),

  deleteOwnershipSubscription: (teamId: string, subscriptionId: string) =>
    sendJson<{ message: string; teamId: string; subscriptionId: string }>(
      `/api/ownership/teams/${encodeURIComponent(teamId)}/subscriptions/${encodeURIComponent(subscriptionId)}`,
      'DELETE'
    ),

  syncOwnershipFromManagementGroups: () =>
    sendJson<OwnershipManagementGroupSyncResult>('/api/ownership/sync-management-groups', 'POST'),

  acceptOwnershipManagementGroupOverrides: (subscriptionIds: string[]) =>
    sendJson<{ message: string; accepted: number; notFound: number }>(
      '/api/ownership/management-group-overrides',
      'POST',
      { subscriptionIds }
    ),
};
