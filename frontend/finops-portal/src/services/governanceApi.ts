import type { DataHealthOverviewResponse, DataHealthResponse, GovernanceBudget, GovernanceBudgetEvaluation, GovernanceBudgetInput } from '../types/governance';

// Register the existing fetchJson/sendJson functions from services/api.ts at module startup.
// No second MSAL instance, token acquisition, base URL or HTTP implementation is introduced here.
export interface GovernanceTransport {
  get<T>(path: string): Promise<T>;
  send<T>(path: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', body?: unknown): Promise<T>;
}

let transport: GovernanceTransport | undefined;

export function configureGovernanceApi(value: GovernanceTransport): void {
  transport = value;
}

function client(): GovernanceTransport {
  if (!transport) throw new Error('Integração de governança pendente: configure o transporte central da API.');
  return transport;
}

export const governanceApi = {
  getDataHealth(subscriptionId: string, from?: string, to?: string): Promise<DataHealthResponse> {
    const query = new URLSearchParams({ subscriptionId });
    if (from) query.set('from', from);
    if (to) query.set('to', to);
    return client().get(`/api/governance/data-health?${query}`);
  },
  getDataHealthOverview(): Promise<DataHealthOverviewResponse> {
    return client().get('/api/governance/data-health/overview');
  },
  listBudgets(): Promise<GovernanceBudget[]> {
    // A missing endpoint/configuration is an error, never a fabricated empty inventory.
    return client().get('/api/governance/budgets');
  },
  async evaluateBudget(id: string): Promise<GovernanceBudgetEvaluation | null> {
    try {
      return await client().get(`/api/governance/budgets/${encodeURIComponent(id)}`);
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'status' in error && error.status === 404) return null;
      throw error;
    }
  },
  saveBudget(id: string, definition: GovernanceBudgetInput): Promise<GovernanceBudget> {
    return client().send(`/api/governance/budgets/${encodeURIComponent(id)}`, 'PUT', definition);
  },
  deleteBudget(budget: GovernanceBudget): Promise<{ deleted: boolean }> {
    return client().send(`/api/governance/budgets/${encodeURIComponent(budget.id)}?${new URLSearchParams({ version: budget.version })}`, 'DELETE');
  },
};