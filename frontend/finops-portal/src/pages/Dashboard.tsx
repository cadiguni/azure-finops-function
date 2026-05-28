import { DollarSign, Lightbulb, AlertTriangle, TrendingDown, Search, Trash2 } from 'lucide-react';
import Card from '../components/Card';
import { api } from '../services/api';
import { useFetch } from '../hooks/useFetch';
import type { CostAnomaly, RecommendationItem, TeamsResponse } from '../types/api';

function formatCurrency(value: number): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
}

function classifyAction(r: RecommendationItem): string {
  const desc = (r.description ?? '').toLowerCase();
  const type = (r.resourceType ?? '').toLowerCase();
  if (type.includes('disk') && desc.includes('unattached')) return 'Excluir';
  if (type.includes('publicipaddresses')) return 'Excluir';
  return 'Revisar';
}

export default function Dashboard() {
  const today = new Date().toISOString().slice(0, 10);

  const { data: summary, loading: loadingSummary, error: errorSummary } = useFetch(
    () => api.getRecommendations(today),
    [today]
  );

  const { data: anomalies, loading: loadingAnomalies } = useFetch(
    () => api.getAnomalies(today, 3).catch((): CostAnomaly[] => []),
    [today]
  );

  const { data: teamsData } = useFetch(
    () => api.getTeams().catch((): TeamsResponse => ({ teams: [], teamsCount: 0, lastUpdated: '' })),
    []
  );

  // Build subscription id→name map from teams data
  const subNameMap = new Map<string, string>();
  (teamsData?.teams ?? []).forEach(t => {
    t.subscriptionIds.forEach((id, i) => {
      if (t.subscriptionNames?.[i]) subNameMap.set(id, t.subscriptionNames[i]);
    });
  });

  const totalSavings = summary?.totalEstimatedMonthlySavings ?? 0;
  const totalRecommendations = summary?.totalRecommendations ?? 0;
  const anomalyCount = anomalies?.filter(a => a.hasAnomaly).length ?? 0;

  const recs = summary?.recommendations ?? [];
  const toReview = recs.filter(r => classifyAction(r) === 'Revisar').length;
  const toDelete = recs.filter(r => classifyAction(r) === 'Excluir').length;

  return (
    <div className="page">
      <div className="page-header">
        <h2>Dashboard</h2>
        <span className="page-date">{today}</span>
      </div>

      {errorSummary && (
        <div className="alert alert--warning">
          Não foi possível carregar dados. Verifique se a API está rodando.
          <br />
          <small>{errorSummary}</small>
        </div>
      )}

      <div className="cards-grid">
        <Card
          title="Economia Potencial Mensal"
          value={loadingSummary ? '...' : formatCurrency(totalSavings)}
          subtitle="Estimativa baseada nas recomendações"
          icon={<DollarSign size={20} />}
          variant="success"
        />
        <Card
          title="Economia Potencial Anual"
          value={loadingSummary ? '...' : formatCurrency(totalSavings * 12)}
          subtitle="Projeção anual"
          icon={<TrendingDown size={20} />}
          variant="success"
        />
        <Card
          title="Total de Recomendações"
          value={loadingSummary ? '...' : totalRecommendations}
          subtitle={`${summary?.byType?.length ?? 0} tipos de recurso`}
          icon={<Lightbulb size={20} />}
        />
        <Card
          title="Recursos para Revisar"
          value={loadingSummary ? '...' : toReview}
          subtitle="Ação: Investigar / Revisar"
          icon={<Search size={20} />}
          variant="warning"
        />
        <Card
          title="Recursos para Excluir"
          value={loadingSummary ? '...' : toDelete}
          subtitle="Discos órfãos, IPs não utilizados"
          icon={<Trash2 size={20} />}
          variant="danger"
        />
        <Card
          title="Anomalias de Custo"
          value={loadingAnomalies ? '...' : anomalyCount}
          subtitle="Últimos 3 dias"
          icon={<AlertTriangle size={20} />}
          variant={anomalyCount > 0 ? 'danger' : 'default'}
        />
      </div>

      {summary?.byType && summary.byType.length > 0 && (
        <div className="section">
          <h3>Economia por Tipo de Recurso</h3>
          <div className="table-container">
            <table>
              <thead>
                <tr>
                  <th>Tipo</th>
                  <th>Quantidade</th>
                  <th>Economia Estimada</th>
                </tr>
              </thead>
              <tbody>
                {summary.byType.map((item) => (
                  <tr key={item.type}>
                    <td>{item.type}</td>
                    <td>{item.count}</td>
                    <td>{formatCurrency(item.estimatedMonthlySavings)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {summary?.bySubscription && summary.bySubscription.length > 0 && (
        <div className="section">
          <h3>Economia por Subscription</h3>
          <div className="table-container">
            <table>
              <thead>
                <tr>
                  <th>Subscription</th>
                  <th>Quantidade</th>
                  <th>Economia Estimada</th>
                </tr>
              </thead>
              <tbody>
                {summary.bySubscription.map((item) => (
                  <tr key={item.subscriptionId}>
                    <td title={item.subscriptionId}>{subNameMap.get(item.subscriptionId) || item.subscriptionId}</td>
                    <td>{item.count}</td>
                    <td>{formatCurrency(item.estimatedMonthlySavings)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}
