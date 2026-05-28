// Types matching backend models

// Response from GET /api/recommendations (Anonymous)
export interface RecommendationsSummary {
  date: string;
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

export interface RecommendationItem {
  resourceId: string;
  resourceName: string;
  resourceType: string;
  resourceGroup: string;
  subscriptionId: string;
  type: string;
  priority: string;
  description: string;
  recommendation: string;
  estimatedMonthlySavings: number;
  dailyCost: number;
  estimatedMonthlyCost: number;
  confidence: number;
  impact: string;
}

export interface CostAnomaly {
  date: string;
  dailyBudget: number;
  subscriptionId: string;
  subscriptionName: string;
  todayCost: number;
  averageLastDays: number;
  increaseAmount: number;
  increasePercent: number;
  monthlyProjection: number;
  projectedOverBudget: number;
  severity: string;
  hasAnomaly: boolean;
  reasons: string[];
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
