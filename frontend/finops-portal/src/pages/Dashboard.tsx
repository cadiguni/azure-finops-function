import { AlertTriangle, Database, DollarSign, ShieldAlert, Target, TrendingDown } from 'lucide-react';
import Card from '../components/Card';
import { useFetch } from '../hooks/useFetch';
import { api } from '../services/api';

function formatCurrency(value: number | null | undefined, currency = 'BRL'): string {
  return value == null ? '—' : value.toLocaleString('pt-BR', { style: 'currency', currency });
}

function resolveMetric(primary?: number | null, fallback?: number | null): number | null {
  return primary ?? fallback ?? null;
}

export default function Dashboard() {
  const today = new Date().toISOString().slice(0, 10);

  const { data: summary, loading: loadingSummary, error: errorSummary } = useFetch(
    () => api.getDashboardSummary(today),
    [today]
  );

  const { data: teamsData, loading: loadingTeams, error: errorTeams } = useFetch(
    () => api.getDashboardTeams(summary?.date ?? today),
    [summary?.date, today]
  );

  const { data: dbData, loading: loadingDb, error: errorDb } = useFetch(
    () => api.getDashboardDatabases(summary?.date ?? today),
    [summary?.date, today]
  );

  const sameSnapshotDb = dbData?.date === summary?.date
    && dbData?.snapshotRevision === summary?.snapshotRevision ? dbData : null;
  const financial = summary?.financialSummary;
  const currency = financial?.currency ?? 'BRL';
  const teams = teamsData?.date === summary?.date
    && teamsData?.snapshotRevision === summary?.snapshotRevision ? teamsData?.teams ?? [] : [];
  const sqlCostTotal = resolveMetric(sameSnapshotDb?.sqlCostTotal, summary?.sqlCostTotal);
  const cosmosCostTotal = resolveMetric(sameSnapshotDb?.cosmosCostTotal, summary?.cosmosCostTotal);
  const databaseCostTotal = resolveMetric(
    sameSnapshotDb?.databaseCostTotal,
    summary?.databaseCostTotal
  );

  return (
    <div className="page">
      <div className="page-header">
        <h2>Dashboard Executivo</h2>
        <span className="page-date">Snapshot: {summary?.date ?? '—'}</span>
      </div>

      {(errorSummary || errorTeams || errorDb) && (
        <div className="alert alert--warning">
          Não foi possível carregar completamente o dashboard.
          <br />
          <small>{errorSummary || errorTeams || errorDb}</small>
        </div>
      )}

      {!loadingSummary && (
        <div className="alert alert--warning">
          {!financial ? 'Qualidade financeira não disponível neste resumo. Aguarde a geração de um novo snapshot.' : (
            <>
              {financial.availability === 'Available' ? 'Custos disponíveis, ainda provisórios.'
                : financial.availability === 'Partial' ? 'Total parcial: há assinaturas sem dados ou com falha.'
                : financial.availability === 'MixedCurrency' ? 'Moedas diferentes: total não agregado.'
                : financial.availability === 'Failed' ? 'Falha na obtenção dos custos.' : 'Sem dados de custo para este snapshot.'}
              <br />
              Cobertura: {financial.availableSubscriptions}/{financial.expectedSubscriptions} assinaturas;
              {' '}{financial.failedSubscriptions} com falha e {financial.missingSubscriptions} sem dados.
              <br />
              Últimas datas de custo por assinatura: {financial.earliestDataAsOf ?? '—'} a {financial.latestDataAsOf ?? '—'}.
              {' '}Relatório: {financial.reportDate ?? '—'}; gerado em: {financial.reportGeneratedAt ?? '—'}.
              {' '}Snapshot: <span title={summary?.snapshotRevision ?? undefined}>{summary?.snapshotRevision?.slice(0, 12) ?? 'legado'}</span>;
              {' '}estado: {financial.finality}; revisões diárias: {Object.keys(financial.dailyRevisions ?? {}).length}.
              {' '}Fontes: {financial.sources.join(', ') || '—'}.
              <br />
              Período financeiro: {summary?.periodFrom && summary?.periodTo
                ? `${summary.periodFrom} a ${summary.periodTo}`
                : 'ciclos de faturamento variam por assinatura'}.
            </>
          )}
        </div>
      )}

      <div className="cards-grid">
        <Card
          title={financial?.availability === 'Partial' ? 'Custo dos ciclos (parcial)' : 'Custo dos ciclos'}
          value={loadingSummary ? '...' : formatCurrency(financial?.totalCost, currency)}
          subtitle="Soma dos ciclos de cada assinatura; não é o mês-calendário"
          icon={<DollarSign size={20} />}
        />
        <Card
          title="Projeção dos ciclos"
          value={loadingSummary ? '...' : formatCurrency(financial?.monthlyProjection, currency)}
          subtitle="Estimativa para os ciclos; mesma cobertura do custo"
          icon={<TrendingDown size={20} />}
          variant="warning"
        />
        <Card
          title="Economia potencial ativa"
          value={loadingSummary ? '...' : formatCurrency(summary?.potentialMonthlySavings ?? 0, currency)}
          subtitle={`Somente valores quantificados | Anual: ${formatCurrency(summary?.potentialAnnualSavings ?? 0, currency)}`}
          icon={<Target size={20} />}
          variant="success"
        />
        <Card
          title="Economia estimada — resolvidas"
          value={loadingSummary ? '...' : formatCurrency(summary?.resolvedMonthlySavings ?? 0, currency)}
          subtitle={`False Positive: ${formatCurrency(summary?.falsePositiveMonthlySavings ?? 0, currency)}`}
          icon={<ShieldAlert size={20} />}
          variant="success"
        />
        <Card
          title="Recomendações ativas"
          value={loadingSummary ? '...' : summary?.activeRecommendations ?? 0}
          subtitle={`Sem owner: ${summary?.resourcesWithoutOwner ?? 0}`}
          icon={<AlertTriangle size={20} />}
          variant="danger"
        />
        <Card
          title="Anomalias críticas"
          value={loadingSummary ? '...' : summary?.criticalAnomalyCount ?? 0}
          subtitle={`Total de anomalias: ${summary?.anomalyCount ?? 0}`}
          icon={<AlertTriangle size={20} />}
          variant={(summary?.criticalAnomalyCount ?? 0) > 0 ? 'danger' : 'default'}
        />
        <Card
          title="Custo observado dos bancos analisados"
          value={loadingDb ? '...' : formatCurrency(databaseCostTotal, currency)}
          subtitle={`SQL: ${formatCurrency(sqlCostTotal, currency)} | Cosmos: ${formatCurrency(cosmosCostTotal, currency)}`}
          icon={<Database size={20} />}
          variant="warning"
        />
      </div>

      <div className="section">
        <h3>Bancos de Dados</h3>
        <p>Valores associados às recomendações, não ao inventário completo. Podem incluir custos compartilhados e não devem ser somados ao custo dos ciclos. Economia implementada ainda exige validação.</p>
        <div className="table-container">
          <table className="dashboard-table dashboard-table--manual">
            <thead>
              <tr>
                <th>Métrica</th>
                <th>Valor</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Custo observado associado — SQL</td>
                <td>{loadingDb ? '...' : formatCurrency(sqlCostTotal, currency)}</td>
              </tr>
              <tr>
                <td>Custo observado associado — Cosmos</td>
                <td>{loadingDb ? '...' : formatCurrency(cosmosCostTotal, currency)}</td>
              </tr>
              <tr>
                <td>Recomendações ativas em SQL</td>
                <td>{loadingDb ? '...' : sameSnapshotDb?.activeSqlRecommendations ?? '—'}</td>
              </tr>
              <tr>
                <td>Recomendações ativas em Cosmos</td>
                <td>{loadingDb ? '...' : sameSnapshotDb?.activeCosmosRecommendations ?? '—'}</td>
              </tr>
              <tr>
                <td>Custos com baixa confiança</td>
                <td>{loadingDb ? '...' : sameSnapshotDb?.lowConfidenceCostCount ?? '—'}</td>
              </tr>
              <tr>
                <td>Custos não associados</td>
                <td>{loadingDb ? '...' : sameSnapshotDb?.notMatchedCostCount ?? '—'}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div className="section">
        <h3>Resumo por time</h3>
        <div className="table-container">
          <table className="dashboard-table dashboard-table--manual">
            <thead>
              <tr>
                <th>Time</th>
                <th>Custo</th>
                <th>Economia potencial</th>
                <th>Recomendações</th>
                <th>Anomalias</th>
                <th>Sem owner</th>
              </tr>
            </thead>
            <tbody>
              {!loadingTeams && teams.map((team) => (
                <tr key={team.id}>
                  <td>{team.name || team.id}</td>
                  <td>
                    {team.snapshotRevision === summary?.snapshotRevision
                      ? formatCurrency(team.financialSummary?.totalCost, team.financialSummary?.currency ?? currency) : '—'}
                    {team.financialSummary?.availability === 'Partial' && ' (parcial)'}
                  </td>
                  <td>{formatCurrency(team.potentialMonthlySavings, team.financialSummary?.currency ?? currency)}</td>
                  <td>{team.activeRecommendations}</td>
                  <td>{team.anomalyCount} (críticas: {team.criticalAnomalyCount})</td>
                  <td>{team.resourcesWithoutOwner}</td>
                </tr>
              ))}
              {loadingTeams && (
                <tr>
                  <td colSpan={6} className="text-center">Carregando times...</td>
                </tr>
              )}
              {!loadingTeams && teams.length === 0 && (
                <tr>
                  <td colSpan={6} className="text-center">Nenhum summary por time disponível para esta data.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
