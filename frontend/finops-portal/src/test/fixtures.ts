import type { CurrentUserInfo } from '../types/api';
import type { DataHealthResponse, GovernanceBudget, GovernanceBudgetEvaluation } from '../types/governance';

export const reader: CurrentUserInfo = {
  userId: 'reader', userPrincipalName: 'reader@example.test', displayName: 'Reader',
  roles: ['FinOps.Reader'], isReader: true, isAdmin: false,
};
export const admin: CurrentUserInfo = { ...reader, userId: 'admin', roles: ['FinOps.Admin'], isAdmin: true };

export function budget(): GovernanceBudget {
  return { id: 'budget-1', version: '"budget-etag"', updatedAt: '2026-09-10T12:00:00Z', updatedBy: 'admin',
    definition: { name: 'DEV mensal', subscriptionId: '11111111-1111-1111-1111-111111111111', month: '2026-09', currency: 'BRL', amount: 1000, costCenter: null } };
}

export function budgetEvaluation(value = budget()): GovernanceBudgetEvaluation {
  return { budget: value, notificationDelivery: 'notConfigured', publicationMode: 'Cutover', observedCost: 400,
    consumptionPercent: 40, estimatedMonthEnd: 1500, forecastReliable: false, forecastExceedsBudget: true,
    reachedThresholds: [], expectedDays: 8, observedDays: 4, missingDates: ['2026-09-02'], dataAsOf: '2026-09-08',
    warnings: ['Há dias ausentes na consolidação.'] };
}

export function dataHealth(): DataHealthResponse {
  return { subscriptionId: '11111111-1111-1111-1111-111111111111', publicationMode: 'Cutover', from: '2026-09-01', to: '2026-09-08',
    checkedAt: '2026-09-10T12:00:00Z', status: 'unknown', lastImportedAt: null, latestUsageDate: null, isStale: null,
    coveragePercent: null, availableDates: null, missingDates: null, lastPublicationStatus: null,
    historicalFailureCount: null, lastCollectorRunAt: null, queueStatus: 'unavailable', timerStatus: 'unavailable', warnings: [] };
}

export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => { resolve = resolvePromise; reject = rejectPromise; });
  return { promise, resolve, reject };
}
