// Types matching backend models
export type * from './governance';
export type * from './migrations';

// Response from GET /api/recommendations (Anonymous)
export interface RecommendationsSummary {
  date: string;
  requestedDate?: string;
  snapshotRevision?: string | null;
  financialSummary?: CostReportFinancialSummary | null;
  periodFrom?: string;
  periodTo?: string;
  totalRecommendations: number;
  totalEstimatedMonthlySavings: number;
  totalEstimatedAnnualSavings: number;
  byType: RecommendationByType[];
  bySubscription: RecommendationBySubscription[];
  recommendations: RecommendationItem[];
}

export interface RecommendationByType {
  type: string;
  count: number;
  estimatedMonthlySavings: number;
}

export interface RecommendationBySubscription {
  subscriptionId: string;
  count: number;
  estimatedMonthlySavings: number;
}

export interface CostReadProvenance {
  datasetRevision?: string | null;
  dailyRevisions: Record<string, string>;
  costBasis: string;
  source: string;
  dataAsOf?: string | null;
  importCompletedAt?: string | null;
  exportRunId?: string | null;
  publicationMode?: string | null;
  usedFallback: boolean;
  fallbackReason?: string | null;
  requestedFrom: string;
  requestedTo: string;
  availableDates: string[];
}

export interface RecommendationItem {
  id: string;
  resourceId: string;
  resourceName: string;
  resourceType: string;
  resourceGroup: string;
  subscriptionId: string;
  teamId?: string | null;
  type: string;
  severity?: string;
  priority: string;
  description: string;
  recommendation: string;
  estimatedMonthlySavings: number;
  savingsStatus?: 'Quantified' | 'NotEstimated' | 'ExcludedFromTotal' | string;
  dailyCost: number;
  estimatedMonthlyCost: number;
  costAmount?: number | null;
  observedCost?: number | null;
  costProvenance?: CostReadProvenance | null;
  costCurrency?: string;
  costPeriodFrom?: string | null;
  costPeriodTo?: string | null;
  costSource?: string;
  costMatchConfidence?: string;
  costMatchedResourceId?: string | null;
  costNote?: string | null;
  confidence: number;
  impact: string;
  firstDetectedAt?: string | null;
    lastDetectedAt?: string | null;
    lastEvaluationDate?: string | null;
  occurrenceCount?: number;
  status: string;
  statusSource?: string;
  hasManualAction?: boolean;
  statusReason?: string | null;
  statusUpdatedBy?: string | null;
  statusUpdatedAt?: string | null;
  lastStatusUpdatedBy?: string | null;
  lastStatusUpdatedAt?: string | null;
  snoozeUntil?: string | null;
  metadata?: Record<string, unknown>;
}

export interface RecommendationOccurrence {
  detectedAt: string;
  resourceId: string;
  subscriptionId: string;
  estimatedMonthlySavings: number;
  severity: string;
  description: string;
}

export interface RecommendationHistoryEntry {
  id: string;
  action: string;
  performedBy: string;
  performedAt: string;
  before?: unknown;
  after?: unknown;
  metadata?: Record<string, unknown>;
}

export interface RecommendationDetailResponse {
  id: string;
  recommendation: RecommendationItem;
  status?: {
    id: string;
    status: string;
    reason?: string | null;
    snoozeUntil?: string | null;
    updatedBy?: string | null;
    updatedAt?: string | null;
    createdAt?: string | null;
  } | null;
  ownership?: {
    teamId?: string | null;
    subscriptionId?: string | null;
  };
  firstDetectedAt?: string | null;
  lastDetectedAt?: string | null;
  occurrenceCount?: number;
  isRecurring?: boolean;
  hasSufficientHistory?: boolean;
  historyWindowDays?: number;
  occurrencesRecent?: RecommendationOccurrence[];
  auditLogs?: RecommendationHistoryEntry[];
}

export interface CostAnomaly {
  date: string;
  requestedDate?: string;
  analysisDate?: string;
  costSource?: string;
  costDataAsOf?: string;
  costUsedFallback?: boolean;
  costFallbackReason?: string | null;
  dataStatus: 'Consolidated' | 'InsufficientData' | 'NoCostData' | 'QueryFailed';
  latestDailyCost?: number;
  baselineAverage?: number;
  dailyBudget: number;
  subscriptionId: string;
  subscriptionName: string;
  todayCost: number;
  averageLastDays: number;
  increaseAmount: number;
  increasePercent: number;
  monthlyProjection: number;
  projectedOverBudget: number;
  currentCycleCost: number;
  currentCycleStartDate: string;
  currentCycleTurnDate: string;
  currentCycleElapsedDays: number;
  currentCycleRemainingDays: number;
  currentCycleDailyAverage: number;
  currentCycleSource?: string;
  currentCycleConfidence?: string;
  currentCycleNote?: string;
  severity: string;
  hasAnomaly: boolean;
  reasons: string[];
  teamId?: string | null;
  teamName?: string | null;
}

export interface CostAnomalyRunResponse {
  runId: string;
  message: string;
  queue: string;
}

export interface CostAnomalyRunStatus {
  runId: string;
  status: 'Running' | 'Completed';
  totalSubscriptions: number;
  completedSubscriptions: number;
  failedSubscriptions: number;
  requestedDate: string;
  updatedAt: string;
  completedAt?: string | null;
  summaryGeneratedAt?: string | null;
}

export interface CostAllocationSummary {
  source: string;
  subscriptionId: string;
  usageDate: string;
  currency: string;
  totalCost: number;
  allocatedResourceCost: number;
  unallocatedCost: number;
}

export interface CostApiResponse<T> {
  dataAsOf?: string | null;
  source: string;
  items: T[];
  allocation: CostAllocationSummary[];
  sourceByDate?: Record<string, string>;
  availableDates?: string[];
  missingDates?: string[];
  requestedDate?: string | null;
  totalCount?: number | null;
  page?: number | null;
  pageSize?: number | null;
}

export interface CostByServiceRow {
  label: string;
  totalCost: number;
  currency: string;
  count: number;
  subscriptionId?: string | null;
}

export interface CostTrendRow {
  date: string;
  totalCost: number;
  currency: string;
}

export interface CostByResourceRow {
  label: string;
  resourceId: string;
  serviceName: string;
  resourceGroup: string;
  resourceLocation: string;
  resourceType: string;
  tags: string;
  totalCost: number;
  currency: string;
  count: number;
  subscriptionId?: string | null;
  dataAsOf?: string | null;
  source?: string | null;
  isStale?: boolean;
}

export type CostAllocationRuleType = 'ResourceGroup' | 'NamePrefix' | 'Service' | 'TagAlias';

export type CostAllocationSource =
  | 'ResourceGroupRule'
  | 'NamePrefixRule'
  | 'ServiceRule'
  | 'TagAlias'
  | 'Tag'
  | 'Unassigned';

export interface CostAllocationRule {
  id: string;
  type: CostAllocationRuleType;
  match: string;
  costCenter: string;
  subscriptionId: string;
  enabled: boolean;
  note: string;
  createdAt: string;
  createdBy: string;
  updatedAt: string;
  updatedBy: string;
}

export interface CostAllocationRulesConfig {
  version: number;
  updatedAt: string;
  updatedBy: string;
  rules: CostAllocationRule[];
}

export interface CostAllocationResourceItem {
  resourceId: string;
  label: string;
  serviceName: string;
  resourceGroup: string;
  resourceType: string;
  subscriptionId: string;
  totalCost: number;
  currency: string;
  source: CostAllocationSource;
  ruleId?: string | null;
  tagValue?: string | null;
  overridesTag: boolean;
}

export interface CostAllocationResourceGroup {
  resourceGroup: string;
  subscriptionId: string;
  totalCost: number;
  resourceCount: number;
  resources: CostAllocationResourceItem[];
}

export interface CostAllocationGroup {
  costCenter: string;
  variants: string[];
  totalCost: number;
  percent: number;
  resourceCount: number;
  manualCost: number;
  resourceGroups: CostAllocationResourceGroup[];
}

export interface CostAllocationBreakdown {
  from: string;
  to: string;
  currency: string;
  tagName: string;
  subscriptionId?: string | null;
  subscriptionIds: string[];
  generatedAt: string;
  daysWithoutData: number;
  totalCost: number;
  assignedCost: number;
  unassignedCost: number;
  coveragePercent?: number | null;
  manualCost: number;
  rulesApplied: number;
  costCenters: CostAllocationGroup[];
  unassigned: CostAllocationGroup;
}

export interface CostByResourceGroupRow {
  resourceGroup: string;
  subscriptionId: string;
  totalCost: number;
  currency: string;
  resourceCount: number;
  serviceCount: number;
  topService: string;
  dataAsOf?: string | null;
  isStale?: boolean;
}

export interface CostDatabaseRange {
  from: string;
  to: string;
  previousFrom: string;
  previousTo: string;
}

export interface CostDatabaseEngineTotal {
  engine: 'Sql' | 'Cosmos' | string;
  totalCost: number;
  previousTotalCost: number | null;
  resourceCount: number;
}

export interface CostDatabaseSubscriptionTotal {
  subscriptionId: string;
  totalCost: number;
  sqlCost: number;
  cosmosCost: number;
  subscriptionTotalCost: number | null;
  shareOfSubscriptionPercent: number | null;
  resourceCount: number;
}

export interface CostDatabaseResourceTotal {
  resourceId: string;
  label: string;
  engine: 'Sql' | 'Cosmos' | string;
  resourceGroup: string;
  resourceType: string;
  subscriptionId: string;
  totalCost: number;
  previousTotalCost: number | null;
  daysWithCost: number;
}

export interface CostDatabaseDailyPoint {
  date: string;
  sqlCost: number;
  cosmosCost: number;
}

export interface CostDatabaseOverview {
  range: CostDatabaseRange;
  dataAsOf?: string | null;
  source: string;
  currency: string;
  daysWithoutData: number;
  daysWithData: number;
  expectedDays: number;
  previousDaysWithoutData: number;
  isLatestSnapshotFallback: boolean;
  engines: CostDatabaseEngineTotal[];
  subscriptions: CostDatabaseSubscriptionTotal[];
  resources: CostDatabaseResourceTotal[];
  daily: CostDatabaseDailyPoint[];
}

export interface ReportMetadata {
  date: string;
  hasHtml: boolean;
  hasCsv: boolean;
  subscriptions: string[];
}

export interface TeamInfo {
  id: string;
  name: string;
  email: string;
  subscriptionsCount: number;
  subscriptionIds: string[];
  subscriptionNames: string[];
}

export interface TeamsResponse {
  teamsCount: number;
  lastUpdated: string;
  teams: TeamInfo[];
}

export interface SubscriptionOwnership {
  subscriptionId: string;
  subscriptionName: string;
  environment: string;
  enabled: boolean;
  source?: 'manual' | 'managementGroup';
  syncedAt?: string | null;
  managementGroupOverride?: boolean;
}

export interface UpdateSubscriptionOwnershipPayload extends SubscriptionOwnership {
  targetTeamId?: string;
}

export interface TeamOwnership {
  id: string;
  name: string;
  requiredRole: string;
  subscriptionsCount: number;
  subscriptions: SubscriptionOwnership[];
}

export interface OwnershipConfig {
  version: number;
  updatedAt: string;
  updatedBy: string;
  lastManagementGroupSyncAt?: string | null;
  lastManagementGroupSyncAdded?: number;
  lastManagementGroupSyncCreatedTeams?: number;
  lastManagementGroupSyncConflicts?: number;
  teamsCount: number;
  teams: TeamOwnership[];
}

export interface OwnershipManagementGroupSyncResult {
  message: string;
  totalSubscriptionsDiscovered: number;
  totalSubscriptionsWithParent?: number;
  added: number;
  createdTeams: number;
  movedAutomaticAssignments: number;
  removedAutomaticTeams: number;
  skippedAlreadyAssigned: number;
  conflicts: number;
  conflictDetails: OwnershipManagementGroupSyncConflict[];
  skippedWithoutTeamMatch: number;
  skippedWithoutParent: number;
}

export interface OwnershipManagementGroupSyncConflict {
  subscriptionId: string;
  subscriptionName: string;
  currentTeamId: string;
  currentTeamName: string;
  managementGroupId: string;
  managementGroupName: string;
  suggestedTeamId: string;
  suggestedTeamName: string;
}

export interface BillingPeriodOverride {
  subscriptionId: string;
  billingCycleStartDay: number;
  updatedAt: string;
  updatedBy: string;
}

export interface CurrentUserInfo {
  userId: string;
  userPrincipalName: string;
  displayName: string;
  roles: string[];
  isReader: boolean;
  isAdmin: boolean;
}

export interface CostReportFinancialSummary {
  totalCost: number | null;
  monthlyProjection: number | null;
  currency: string | null;
  availability: 'Missing' | 'Failed' | 'Partial' | 'Available' | 'MixedCurrency';
  finality: string;
  periodKind: string;
  reportDate: string | null;
  reportGeneratedAt: string | null;
  earliestDataAsOf: string | null;
  latestDataAsOf: string | null;
  expectedSubscriptions: number;
  availableSubscriptions: number;
  failedSubscriptions: number;
  missingSubscriptions: number;
  dailyRevisions: Record<string, string>;
  sources: string[];
}

export interface DashboardSummaryResponse {
  date: string;
  snapshotRevision?: string | null;
  periodId: string;
  periodFrom: string;
  periodTo: string;
  totalCost: number | null;
  monthlyProjection: number | null;
  financialSummary?: CostReportFinancialSummary | null;
  activeRecommendations: number;
  potentialMonthlySavings: number;
  potentialAnnualSavings: number;
  resolvedMonthlySavings: number;
  falsePositiveMonthlySavings: number;
  anomalyCount: number;
  criticalAnomalyCount: number;
  resourcesWithoutOwner: number;
  databaseCostTotal: number | null;
  sqlCostTotal: number | null;
  cosmosCostTotal: number | null;
  recommendationStatusCounts?: Record<string, number>;
  updatedAt: string;
}

export interface DashboardTeamItem {
  snapshotRevision?: string | null;
  id: string;
  name?: string;
  totalCost: number | null;
  monthlyProjection: number | null;
  financialSummary?: CostReportFinancialSummary | null;
  activeRecommendations: number;
  potentialMonthlySavings: number;
  potentialAnnualSavings: number;
  anomalyCount: number;
  criticalAnomalyCount: number;
  resourcesWithoutOwner: number;
  sqlCostTotal: number | null;
  cosmosCostTotal: number | null;
  updatedAt: string;
}

export interface DashboardTeamsResponse {
  date: string;
  snapshotRevision?: string | null;
  count: number;
  teams: DashboardTeamItem[];
}

export interface DashboardDatabasesResponse {
  date: string;
  snapshotRevision?: string | null;
  sqlCostTotal: number | null;
  cosmosCostTotal: number | null;
  databaseCostTotal: number | null;
  activeSqlRecommendations: number;
  activeCosmosRecommendations: number;
  lowConfidenceCostCount: number;
  notMatchedCostCount: number;
  updatedAt: string;
}
