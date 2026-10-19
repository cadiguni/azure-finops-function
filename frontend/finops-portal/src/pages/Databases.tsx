import { useMemo, useState } from 'react';
import Card from '../components/Card';
import CostTrendChart from '../components/cost/CostTrendChart';
import { api } from '../services/api';
import { useFetch } from '../hooks/useFetch';
import { useTableSort } from '../hooks/useTableSort';
import type { CostDatabaseResourceTotal, RecommendationItem } from '../types/api';
import { Database, HardDrive } from 'lucide-react';

const WINDOW_OPTIONS = [7, 30, 60, 90];
const ACTIVE_STATUSES = new Set(['active', 'new', 'inreview', 'accepted', 'approvedforcleanup']);
const SEVERITY_ORDER: Record<string, number> = { Critical: 0, High: 1, Medium: 2, Low: 3, Info: 4 };

type EngineFilter = 'all' | 'Sql' | 'Cosmos';
type ResourceSortKey = 'label' | 'engine' | 'resourceGroup' | 'totalCost' | 'daysWithCost' | 'variation';
type SubscriptionSortKey = 'subscriptionId' | 'sqlCost' | 'cosmosCost' | 'totalCost' | 'subscriptionTotalCost' | 'share';

const RESOURCE_MONEY_COLUMNS: ResourceSortKey[] = ['totalCost', 'daysWithCost', 'variation'];
const SUBSCRIPTION_MONEY_COLUMNS: SubscriptionSortKey[] = ['sqlCost', 'cosmosCost', 'totalCost', 'subscriptionTotalCost', 'share'];

function shiftDays(key: string, amount: number): string {
  const [year, month, day] = key.split('-').map(Number);
  const date = new Date(year, month - 1, day + amount);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(
    date.getDate()
  ).padStart(2, '0')}`;
}

function todayKey(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(
    now.getDate()
  ).padStart(2, '0')}`;
}

function normalizeResourceId(value?: string | null): string {
  return (value ?? '').trim().replace(/\/$/, '').toLowerCase();
}

function engineLabel(engine: string): string {
  return engine === 'Sql' ? 'Azure SQL' : engine === 'Cosmos' ? 'Cosmos DB' : engine;
}

export default function Databases() {
  const [windowDays, setWindowDays] = useState(30);
  const [engineFilter, setEngineFilter] = useState<EngineFilter>('all');
  const [subscriptionFilter, setSubscriptionFilter] = useState('');
  const [searchText, setSearchText] = useState('');
  const [minCost, setMinCost] = useState('');
  const [onlyIncomplete, setOnlyIncomplete] = useState(false);

  // The collector runs a couple of days behind, so the window ends before today.
  const rangeEnd = useMemo(() => shiftDays(todayKey(), -2), []);
  const rangeStart = useMemo(() => shiftDays(rangeEnd, -(windowDays - 1)), [rangeEnd, windowDays]);

  const { data: overview, loading, error } = useFetch(
    () => api.getCostDatabases(rangeStart, rangeEnd, subscriptionFilter || undefined),
    [rangeStart, rangeEnd, subscriptionFilter]
  );

  const { data: ownershipConfig } = useFetch(
    () => api.getOwnershipTeams().catch(() => null),
    []
  );

  const { data: recommendations } = useFetch(
    () => api.getRecommendations(rangeEnd).catch(() => null),
    [rangeEnd]
  );

  const subscriptionNames = useMemo(() => {
    const map = new Map<string, string>();
    ownershipConfig?.teams.forEach((team) => {
      team.subscriptions.forEach((subscription) => {
        map.set(subscription.subscriptionId, subscription.subscriptionName);
      });
    });
    return map;
  }, [ownershipConfig]);

  // Signals stay separate from cost: an active recommendation explains a resource, never re-prices it.
  const signalByResource = useMemo(() => {
    const map = new Map<string, RecommendationItem>();
    (recommendations?.recommendations ?? [])
      .filter((item) => ACTIVE_STATUSES.has((item.status ?? 'New').trim().toLowerCase()))
      .forEach((item) => {
        const key = normalizeResourceId(item.costMatchedResourceId || item.resourceId);
        if (!key) return;
        const current = map.get(key);
        const rank = SEVERITY_ORDER[item.severity ?? item.priority] ?? 9;
        const currentRank = current ? SEVERITY_ORDER[current.severity ?? current.priority] ?? 9 : 99;
        if (!current || rank < currentRank) map.set(key, item);
      });
    return map;
  }, [recommendations]);

  const currencyFormatter = useMemo(() => {
    try {
      return new Intl.NumberFormat('pt-BR', {
        style: 'currency',
        currency: overview?.currency || 'BRL',
      });
    } catch {
      return new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });
    }
  }, [overview?.currency]);

  const engines = overview?.engines ?? [];
  const dailyPoints = overview?.daily ?? [];
  const subscriptionTotals = overview?.subscriptions ?? [];
  const sql = engines.find((engine) => engine.engine === 'Sql');
  const cosmos = engines.find((engine) => engine.engine === 'Cosmos');
  const totalCost = engines.reduce((sum, engine) => sum + (engine.totalCost ?? 0), 0);
  const previousTotal = engines.reduce((sum, engine) => sum + (engine.previousTotalCost ?? 0), 0);
  const hasPrevious = engines.some((engine) => engine.previousTotalCost != null);
  // A variation only has meaning when both windows cover every requested day. Showing a numeric
  // delta for a partial historical window would turn the data-quality warning into a bad decision.
  const hasComparablePrevious = hasPrevious
    && (overview?.daysWithoutData ?? 0) === 0
    && (overview?.previousDaysWithoutData ?? 0) === 0;

  const minCostValue = Number.parseFloat(minCost.replace(',', '.'));
  const hasMinCost = Number.isFinite(minCostValue) && minCostValue > 0;
  const query = searchText.trim().toLowerCase();
  const expectedDays = overview?.expectedDays ?? windowDays;
  const filteredResources = useMemo<CostDatabaseResourceTotal[]>(() => {
    return (overview?.resources ?? [])
      .filter((item) => engineFilter === 'all' || item.engine === engineFilter)
      .filter((item) => !query
        || [item.label, item.resourceId, item.resourceGroup].some((field) => (field ?? '').toLowerCase().includes(query)))
      .filter((item) => !hasMinCost || item.totalCost >= minCostValue)
      // Período incompleto é a causa mais comum de um total abaixo do portal do Azure: dá para
      // isolar só os recursos afetados em vez de conferir linha a linha.
      .filter((item) => !onlyIncomplete || item.daysWithCost < expectedDays);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [overview, engineFilter, query, minCost, onlyIncomplete, expectedDays]);

  const hiddenResources = (overview?.resources.length ?? 0) - filteredResources.length;
  const hasActiveFilter = engineFilter !== 'all' || Boolean(query) || hasMinCost || onlyIncomplete;

  const resourceSort = useTableSort<CostDatabaseResourceTotal, ResourceSortKey>(
    filteredResources,
    {
      label: (resource) => resource.label,
      engine: (resource) => resource.engine,
      resourceGroup: (resource) => resource.resourceGroup,
      totalCost: (resource) => resource.totalCost,
      daysWithCost: (resource) => resource.daysWithCost,
      // Sem base anterior não há variação: a linha vai para o fim em vez de fingir 0%.
      variation: (resource) => resource.previousTotalCost == null || resource.previousTotalCost === 0
        ? null
        : (resource.totalCost - resource.previousTotalCost) / resource.previousTotalCost,
    },
    { key: 'totalCost', direction: 'desc' },
    RESOURCE_MONEY_COLUMNS
  );

  const subscriptionSort = useTableSort<typeof subscriptionTotals[number], SubscriptionSortKey>(
    subscriptionTotals,
    {
      subscriptionId: (item) => subscriptionNames.get(item.subscriptionId) ?? item.subscriptionId,
      sqlCost: (item) => item.sqlCost,
      cosmosCost: (item) => item.cosmosCost,
      totalCost: (item) => item.totalCost,
      subscriptionTotalCost: (item) => item.subscriptionTotalCost ?? null,
      share: (item) => item.shareOfSubscriptionPercent ?? null,
    },
    { key: 'totalCost', direction: 'desc' },
    SUBSCRIPTION_MONEY_COLUMNS
  );

  const formatVariation = (current: number, previous: number | null): string => {
    if (previous == null) return 'sem base anterior';
    const delta = current - previous;
    const percent = previous > 0 ? (delta / previous) * 100 : null;
    const signal = delta > 0 ? '+' : '';
    return `${signal}${currencyFormatter.format(delta)}${
      percent == null ? '' : ` (${signal}${percent.toFixed(1)}%)`
    }`;
  };

  // Options come from ownership, not from the response: filtering by one subscription would
  // otherwise shrink the list to that single option and trap the user in it.
  const subscriptionOptions = useMemo(() => {
    const fromOwnership = [...subscriptionNames.keys()];
    if (fromOwnership.length > 0) return fromOwnership;
    return (overview?.subscriptions ?? []).map((subscription) => subscription.subscriptionId);
  }, [subscriptionNames, overview]);

  return (
    <div className="page">
      <div className="page-header">
        <h2>Bancos de Dados</h2>
        <span className="page-date">
          {overview
            ? `${overview.range?.from ?? '—'} a ${overview.range?.to ?? '—'} · ${filteredResources.length} recursos`
            : '...'}
        </span>
      </div>

      <div className="alert">
        Esta tela mostra <strong>custo observado</strong> de Azure SQL e Cosmos DB — fato financeiro
        vindo do Cost Management. Sinais de dimensionamento e estimativas de economia aparecem
        identificados como tal, e não alteram os valores de custo.
        {overview && (
          <>
            {' '}Fonte: {overview.source}
            {overview.dataAsOf ? ` · dados até ${overview.dataAsOf.slice(0, 10)}` : ''}
            {overview.daysWithoutData > 0 ? ` · ${overview.daysWithoutData} dia(s) sem dados` : ''}.
            {overview.isLatestSnapshotFallback && (
              <> Exibindo o último snapshot disponível; os valores não representam a soma do período selecionado.</>
            )}
          </>
        )}
      </div>

      {overview && overview.daysWithoutData > 0 && !overview.isLatestSnapshotFallback && (
        <div className="alert alert--warning">
          Período incompleto: <strong>{overview.daysWithData} de {overview.expectedDays} dias</strong> têm
          custo consolidado. Os totais abaixo somam só esses dias, então ficam <strong>abaixo</strong> do
          que o portal do Azure mostra para o mesmo intervalo — a diferença é dado ausente, não custo a
          menos. Dias sem consolidação são preenchidos pelo timer de rollup (no máximo 7 por requisição
          são reconstruídos sob demanda).
          {overview.previousDaysWithoutData > 0 && (
            <> A janela anterior também tem {overview.previousDaysWithoutData} dia(s) sem dados, então a
            coluna de variação compara períodos de tamanhos diferentes.</>
          )}
        </div>
      )}

      <div className="filters filters--compact">
        <div className="filter-group">
          <label htmlFor="db-window">Período</label>
          <select
            id="db-window"
            value={windowDays}
            onChange={(event) => setWindowDays(Number(event.target.value))}
          >
            {WINDOW_OPTIONS.map((option) => (
              <option key={option} value={option}>{`Últimos ${option} dias`}</option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="db-engine">Serviço</label>
          <select
            id="db-engine"
            value={engineFilter}
            onChange={(event) => setEngineFilter(event.target.value as EngineFilter)}
          >
            <option value="all">Todos</option>
            <option value="Sql">Azure SQL</option>
            <option value="Cosmos">Cosmos DB</option>
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="db-search">Buscar</label>
          <input
            id="db-search"
            type="search"
            value={searchText}
            placeholder="Banco ou resource group"
            onChange={(event) => setSearchText(event.target.value)}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="db-min-cost">Custo mínimo</label>
          <input
            id="db-min-cost"
            inputMode="decimal"
            value={minCost}
            placeholder="Ex.: 10"
            onChange={(event) => setMinCost(event.target.value)}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="db-subscription">Subscription</label>
          <input
            id="db-subscription"
            list="db-subscription-options"
            value={subscriptionFilter}
            onChange={(event) => setSubscriptionFilter(event.target.value.trim())}
            placeholder="Todas"
          />
          <datalist id="db-subscription-options">
            {subscriptionOptions.map((subscriptionId) => (
              <option key={subscriptionId} value={subscriptionId}>
                {subscriptionNames.get(subscriptionId) ?? subscriptionId}
              </option>
            ))}
          </datalist>
        </div>
        <div className="filter-group recommendations-column-actions">
          <label>&nbsp;</label>
          <label className="text-muted" title="Mostra só bancos cujo período tem dias sem custo consolidado — a causa usual de um total abaixo do portal do Azure.">
            <input
              type="checkbox"
              checked={onlyIncomplete}
              onChange={(event) => setOnlyIncomplete(event.target.checked)}
            />{' '}
            Só período incompleto
          </label>
        </div>
        <div className="filter-group recommendations-column-actions">
          <label>&nbsp;</label>
          <button
            className="btn btn--secondary"
            disabled={!hasActiveFilter && !subscriptionFilter}
            onClick={() => {
              setEngineFilter('all');
              setSearchText('');
              setMinCost('');
              setOnlyIncomplete(false);
              setSubscriptionFilter('');
            }}
          >
            Limpar filtros
          </button>
        </div>
      </div>

      {loading && <div className="loading">Carregando custos de bancos...</div>}
      {error && (
        <div className="alert alert--warning">
          Não foi possível carregar os custos de bancos.
          <br />
          <small>{error}</small>
        </div>
      )}

      {!loading && !error && overview && (
        <>
          <div className="cards-grid">
            <Card
              title="Azure SQL"
              value={currencyFormatter.format(sql?.totalCost ?? 0)}
              subtitle={`${sql?.resourceCount ?? 0} recursos · ${formatVariation(
                sql?.totalCost ?? 0,
                hasComparablePrevious ? sql?.previousTotalCost ?? null : null
              )}`}
              icon={<Database size={20} />}
            />
            <Card
              title="Cosmos DB"
              value={currencyFormatter.format(cosmos?.totalCost ?? 0)}
              subtitle={`${cosmos?.resourceCount ?? 0} recursos · ${formatVariation(
                cosmos?.totalCost ?? 0,
                hasComparablePrevious ? cosmos?.previousTotalCost ?? null : null
              )}`}
              icon={<HardDrive size={20} />}
            />
            <Card
              title="Total em bancos"
              value={currencyFormatter.format(totalCost)}
              subtitle={
                overview.isLatestSnapshotFallback
                  ? `snapshot de ${overview.dataAsOf?.slice(0, 10) ?? 'data não informada'}`
                  : hasComparablePrevious
                  ? `vs. ${overview.range?.previousFrom ?? '—'} a ${overview.range?.previousTo ?? '—'}: ${formatVariation(
                      totalCost,
                      previousTotal
                    )}`
                  : 'sem período anterior comparável'
              }
              variant={hasComparablePrevious && totalCost > previousTotal ? 'warning' : 'success'}
            />
          </div>

          <div className="section">
            <h3>Evolução diária</h3>
            {dailyPoints.length === 0 ? (
              <p className="cost-breakdown-empty">Sem custo de bancos no período.</p>
            ) : (
              <CostTrendChart
                series={[
                  {
                    id: 'sql',
                    label: 'Azure SQL',
                    variant: 'primary',
                    points: dailyPoints.map((point) => ({ date: point.date, value: point.sqlCost ?? 0 })),
                  },
                  {
                    id: 'cosmos',
                    label: 'Cosmos DB',
                    variant: 'secondary',
                    points: dailyPoints.map((point) => ({ date: point.date, value: point.cosmosCost ?? 0 })),
                  },
                ]}
                currency={overview.currency}
              />
            )}
          </div>

          <div className="section">
            <h3>Por assinatura</h3>
            <div className="table-container">
              <table className="dashboard-table">
                <thead>
                  <tr>
                    <th {...subscriptionSort.sortableHeader('subscriptionId')}>Subscription{subscriptionSort.indicator('subscriptionId')}</th>
                    <th {...subscriptionSort.sortableHeader('sqlCost')}>Azure SQL{subscriptionSort.indicator('sqlCost')}</th>
                    <th {...subscriptionSort.sortableHeader('cosmosCost')}>Cosmos DB{subscriptionSort.indicator('cosmosCost')}</th>
                    <th {...subscriptionSort.sortableHeader('totalCost')}>Total em bancos{subscriptionSort.indicator('totalCost')}</th>
                    <th {...subscriptionSort.sortableHeader('subscriptionTotalCost')}>Custo da assinatura{subscriptionSort.indicator('subscriptionTotalCost')}</th>
                    <th {...subscriptionSort.sortableHeader('share')}>Participação{subscriptionSort.indicator('share')}</th>
                  </tr>
                </thead>
                <tbody>
                  {subscriptionSort.sorted.map((subscription) => (
                    <tr key={subscription.subscriptionId}>
                      <td title={subscription.subscriptionId}>
                        {subscriptionNames.get(subscription.subscriptionId) ?? subscription.subscriptionId}
                      </td>
                      <td>{currencyFormatter.format(subscription.sqlCost)}</td>
                      <td>{currencyFormatter.format(subscription.cosmosCost)}</td>
                      <td>{currencyFormatter.format(subscription.totalCost)}</td>
                      <td>
                        {subscription.subscriptionTotalCost == null
                          ? 'desconhecido'
                          : currencyFormatter.format(subscription.subscriptionTotalCost)}
                      </td>
                      <td>
                        {subscription.shareOfSubscriptionPercent == null
                          ? '—'
                          : `${subscription.shareOfSubscriptionPercent.toFixed(1)}%`}
                      </td>
                    </tr>
                  ))}
                  {subscriptionTotals.length === 0 && (
                    <tr>
                      <td colSpan={6} className="text-center">
                        Nenhum custo de banco no período.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>

          <div className="section">
            <div className="section-header">
              <h3>Por recurso</h3>
              {hasActiveFilter && (
                <span className="text-muted">
                  Filtro ativo nesta tabela: {filteredResources.length} de{' '}
                  {overview.resources.length} recursos. Os cards acima seguem somando o período inteiro.
                </span>
              )}
            </div>
            <p className="cost-breakdown-empty">
              Custo é fato financeiro. Sinal vem de recomendação ativa e ainda não usa métricas de uso.
              Economia estimada exige validação antes de virar decisão.
            </p>
            <div className="table-container">
              <table className="dashboard-table">
                <thead>
                  <tr>
                    <th {...resourceSort.sortableHeader('label')}>Recurso{resourceSort.indicator('label')}</th>
                    <th {...resourceSort.sortableHeader('engine')}>Serviço{resourceSort.indicator('engine')}</th>
                    <th {...resourceSort.sortableHeader('resourceGroup')}>Resource Group{resourceSort.indicator('resourceGroup')}</th>
                    <th {...resourceSort.sortableHeader('totalCost')}>Custo no período{resourceSort.indicator('totalCost')}</th>
                    <th {...resourceSort.sortableHeader('daysWithCost')}>Dias com custo{resourceSort.indicator('daysWithCost')}</th>
                    <th {...resourceSort.sortableHeader('variation')}>Variação{resourceSort.indicator('variation')}</th>
                    <th>Sinal</th>
                    <th>Economia estimada</th>
                  </tr>
                </thead>
                <tbody>
                  {resourceSort.sorted.map((resource) => {
                    const signal = signalByResource.get(normalizeResourceId(resource.resourceId));
                    const previousCost = hasComparablePrevious ? resource.previousTotalCost : null;
                    const quantified = signal?.savingsStatus === 'Quantified'
                      || (!signal?.savingsStatus && (signal?.estimatedMonthlySavings ?? 0) > 0);
                    return (
                      <tr key={resource.resourceId || resource.label}>
                        <td title={resource.resourceId}>
                          {resource.label}
                          <span className="cost-breakdown-meta">{resource.resourceType}</span>
                        </td>
                        <td>{engineLabel(resource.engine)}</td>
                        <td>{resource.resourceGroup || '—'}</td>
                        <td>{currencyFormatter.format(resource.totalCost)}</td>
                        <td
                          className={resource.daysWithCost < expectedDays ? 'text-warning' : ''}
                          title="Dias do período em que este recurso teve custo consolidado. Menos dias que o período significa soma parcial."
                        >
                          {resource.daysWithCost} de {expectedDays}
                        </td>
                        <td
                          className={
                            previousCost == null
                              ? ''
                              : resource.totalCost > previousCost
                                ? 'text-danger'
                                : 'text-success'
                          }
                        >
                          {formatVariation(resource.totalCost, previousCost)}
                        </td>
                        <td>
                          {signal ? (
                            <span title={signal.description}>{signal.type}</span>
                          ) : (
                            <span className="cost-breakdown-meta">sem sinal</span>
                          )}
                        </td>
                        <td>
                          {!signal
                            ? '—'
                            : quantified
                              ? `~${currencyFormatter.format(signal.estimatedMonthlySavings)}/mês`
                              : 'não estimada'}
                        </td>
                      </tr>
                    );
                  })}
                  {filteredResources.length === 0 && (
                    <tr>
                      <td colSpan={8} className="text-center">
                        Nenhum recurso de banco com custo no período.
                        {hiddenResources > 0 && ` ${hiddenResources} recurso(s) ocultos pelos filtros.`}
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
