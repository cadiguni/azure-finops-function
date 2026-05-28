import type { CostAnomaly, RecommendationsSummary, TeamsResponse } from '../types/api';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || '';

async function fetchJson<T>(path: string): Promise<T> {
  const res = await fetch(`${API_BASE_URL}${path}`);
  if (!res.ok) {
    throw new Error(`API error ${res.status}: ${res.statusText}`);
  }
  return res.json();
}

export const api = {
  // Health
  health: () => fetchJson<string>('/api/health'),

  // Recommendations (Anonymous - GET /api/recommendations)
  getRecommendations: (date: string, subscriptionId?: string) => {
    const params = new URLSearchParams({ date });
    if (subscriptionId) params.set('subscriptionId', subscriptionId);
    return fetchJson<RecommendationsSummary>(`/api/recommendations?${params}`);
  },

  // Reports
  getReportHtmlUrl: (date: string, subscriptionId?: string, team?: string): string => {
    const params = new URLSearchParams();
    if (date) params.set('date', date);
    if (subscriptionId) params.set('subscription', subscriptionId);
    if (team) params.set('team', team);
    return `${API_BASE_URL}/api/report/html?${params}`;
  },

  getReportCsvUrl: (date: string, subscriptionId?: string, team?: string): string => {
    const params = new URLSearchParams();
    if (date) params.set('date', date);
    if (subscriptionId) params.set('subscription', subscriptionId);
    if (team) params.set('team', team);
    return `${API_BASE_URL}/api/report/csv?${params}`;
  },

  // Cost Anomalies (Anonymous - GET /api/cost-anomalies)
  getAnomalies: (date?: string, days?: number, subscriptionId?: string) => {
    const params = new URLSearchParams();
    if (date) params.set('date', date);
    if (days) params.set('days', days.toString());
    if (subscriptionId) params.set('subscriptionId', subscriptionId);
    return fetchJson<CostAnomaly[]>(`/api/cost-anomalies?${params}`);
  },

  // Teams (Anonymous - GET /api/teams)
  getTeams: () => fetchJson<TeamsResponse>('/api/teams'),
};
