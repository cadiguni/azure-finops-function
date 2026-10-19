export interface DataHealthResponse {
  subscriptionId: string;
  publicationMode: 'Shadow' | 'Cutover';
  from: string;
  to: string;
  checkedAt: string;
  status: 'unavailable' | 'unknown' | 'degraded' | 'incomplete' | 'covered';
  lastImportedAt: string | null;
  latestUsageDate: string | null;
  isStale: boolean | null;
  coveragePercent: number | null;
  availableDates: string[] | null;
  missingDates: string[] | null;
  lastPublicationStatus: string | null;
  historicalFailureCount: number | null;
  lastCollectorRunAt: string | null;
  queueStatus: 'unavailable';
  timerStatus: 'unavailable';
  warnings: string[];
}

export interface DataHealthOverviewRow {
  subscriptionId: string;
  subscriptionName: string | null;
  exportIngestion: boolean;
  publicationMode: 'Shadow' | 'Cutover';
  status: DataHealthResponse['status'];
  coveragePercent: number | null;
  missingDays: number | null;
  latestUsageDate: string | null;
  isStale: boolean | null;
  lastImportedAt: string | null;
  lastPublicationStatus: string | null;
}

export interface DataHealthOverviewResponse {
  from: string;
  to: string;
  checkedAt: string;
  subscriptionSource: string;
  subscriptions: DataHealthOverviewRow[];
  warnings: string[];
}

export interface GovernanceBudgetInput {
  name: string;
  subscriptionId: string;
  month: string;
  currency: string;
  amount: number;
  costCenter: string | null;
  version?: string | null;
}

export interface GovernanceBudget {
  id: string;
  definition: GovernanceBudgetInput;
  updatedAt: string;
  updatedBy: string;
  version: string;
}

export interface GovernanceBudgetEvaluation {
  notificationDelivery: 'notConfigured';
  budget: GovernanceBudget;
  publicationMode: 'Shadow' | 'Cutover';
  observedCost: number | null;
  consumptionPercent: number | null;
  estimatedMonthEnd: number | null;
  forecastReliable: boolean;
  forecastExceedsBudget: boolean | null;
  reachedThresholds: number[];
  expectedDays: number;
  observedDays: number;
  missingDates: string[];
  dataAsOf: string | null;
  warnings: string[];
}