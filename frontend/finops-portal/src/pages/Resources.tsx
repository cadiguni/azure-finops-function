import { Fragment, useEffect, useMemo, useState } from 'react';
import { Boxes, ChevronDown, ChevronRight, Coins, Database } from 'lucide-react';
import Card from '../components/Card';
import { useFetch } from '../hooks/useFetch';
import { useTableSort } from '../hooks/useTableSort';
import { api } from '../services/api';
import type {
  CostApiResponse,
  CostByResourceGroupRow,
  CostByServiceRow,
  CostByResourceRow,
  OwnershipConfig,
  RecommendationItem,
} from '../types/api';

const EMPTY_GROUPS: CostByResourceGroupRow[] = [];
const GROUP_PAGE_SIZE = 50;
const RESOURCE_PAGE_SIZE = 50;

type GroupSortKey = 'resourceGroup' | 'subscriptionId' | 'topService' | 'resourceCount' | 'totalCost' | 'dataAsOf';
type ResourceSortKey = 'label' | 'serviceName' | 'resourceLocation' | 'totalCost' | 'count';

const MONEY_FIRST_GROUP_COLUMNS: GroupSortKey[] = ['resourceCount', 'totalCost', 'dataAsOf'];
const MONEY_FIRST_RESOURCE_COLUMNS: ResourceSortKey[] = ['totalCost', 'count'];

function dateKey(daysAgo: number): string {
  const date = new Date();
  date.setDate(date.getDate() - daysAgo);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

function formatCurrency(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat('pt-BR', { style: 'currency', currency }).format(value);
  } catch {
    return `${currency} ${value.toLocaleString('pt-BR', { maximumFractionDigits: 2 })}`;
  }
}

function sourceLabel(source?: string | null): string {
  if (source === 'CostManagementExport') return 'Cost Management Export';
  if (source === 'CostManagementQueryApi') return 'Cost Management Query API';
  if (source === 'Mixed') return 'Fontes consolidadas';
  return source || 'Origem não informada';
}

function resourceKey(value?: string | null): string {
  return (value ?? '').trim().replace(/\/$/, '').toLowerCase();
}

function usageLabel(item?: RecommendationItem): { label: string; observedAt?: string | null } {
  const metadata = item?.metadata;
  const read = (key: string) => typeof metadata?.[key] === 'number' && Number.isFinite(metadata[key] as number)
    && (metadata[key] as number) >= 0 ? metadata[key] as number : null;
  const cpu = read('avgCpuUsage') ?? read('avgCpuPercentage');
  const memory = read('avgMemoryUsage');
  const requests = read('avgRequests');
  const parts = [cpu != null ? `CPU ${cpu.toFixed(1)}%` : null, memory != null ? `Memória ${memory.toFixed(1)}%` : null, requests != null ? `${requests.toFixed(0)} req/h` : null].filter(Boolean);
  return {
    label: parts.length > 0 ? parts.join(' · ') : 'Métrica não disponível',
    observedAt: item?.lastEvaluationDate ?? item?.lastDetectedAt,
  };
}

function groupKey(group: CostByResourceGroupRow): string {
  return `${group.subscriptionId}|${group.resourceGroup}|${group.currency}`;
}

export default function Resources() {
  const [date, setDate] = useState(() => dateKey(2));
  const [subscriptionFilter, setSubscriptionFilter] = useState('');
  const [serviceFilter, setServiceFilter] = useState('');
  const [searchText, setSearchText] = useState('');
  const [expandedGroup, setExpandedGroup] = useState<string | null>(null);
  const [minCost, setMinCost] = useState('');
  const [page, setPage] = useState(1);

  useEffect(() => {
    setPage(1);
  }, [date, subscriptionFilter, serviceFilter, searchText, minCost]);

  const { data: response, loading, error, refetch } = useFetch(
    () => api.getCostByResourceGroup(date, {
      subscriptionId: subscriptionFilter || undefined,
      service: serviceFilter || undefined,
      resourceGroup: searchText.trim() || undefined,
      minCost: Number.isFinite(Number.parseFloat(minCost.replace(',', '.'))) && Number.parseFloat(minCost.replace(',', '.')) > 0
        ? Number.parseFloat(minCost.replace(',', '.'))
        : undefined,
      page,
      pageSize: GROUP_PAGE_SIZE,
    }),
    [date, subscriptionFilter, serviceFilter, searchText, minCost, page]
  );

  // Os serviços vêm do agregado por serviço, que é barato: a grade por RG não carrega recurso algum.
  const { data: servicesResponse } = useFetch<CostApiResponse<CostByServiceRow> | null>(
    () => api.getCostByService(date, subscriptionFilter || undefined).catch(() => null),
    [date, subscriptionFilter]
  );

  const { data: ownership } = useFetch(() => api.getOwnershipTeams().catch(() => null), []);

  const allGroups = response?.items ?? EMPTY_GROUPS;
  const groups = allGroups;
  const groupSort = useTableSort<CostByResourceGroupRow, GroupSortKey>(
    groups,
    {
      resourceGroup: (group) => group.resourceGroup,
      subscriptionId: (group) => group.subscriptionId,
      topService: (group) => group.topService,
      resourceCount: (group) => group.resourceCount,
      totalCost: (group) => group.totalCost,
      dataAsOf: (group) => group.dataAsOf ?? null,
    },
    { key: 'totalCost', direction: 'desc' },
    MONEY_FIRST_GROUP_COLUMNS
  );
  const services = useMemo<string[]>(() => {
    const labels = (servicesResponse?.items ?? [])
      .map((item) => item.label)
      .filter((label): label is string => Boolean(label));
    return [...new Set(labels)].sort((left, right) => left.localeCompare(right));
  }, [servicesResponse]);
  const subscriptions = useMemo(() => (ownership as OwnershipConfig | null)?.teams
    .flatMap((team) => team.subscriptions)
    .sort((left, right) => left.subscriptionName.localeCompare(right.subscriptionName)) ?? [], [ownership]);
  const snapshotCoverage = useMemo(() => {
    const subscriptionsWithData = new Set(groups
      .map((item) => item.subscriptionId?.trim())
      .filter((subscriptionId): subscriptionId is string => Boolean(subscriptionId)));
    const dates = [...new Set(groups
      .map((item) => item.dataAsOf?.slice(0, 10))
      .filter((dataAsOf): dataAsOf is string => Boolean(dataAsOf)))]
      .sort();
    return { subscriptionsWithData: subscriptionsWithData.size, dates };
  }, [groups]);
  const totalsByCurrency = useMemo(() => {
    const totals = new Map<string, number>();
    groups.forEach((item) => {
      totals.set(item.currency, (totals.get(item.currency) ?? 0) + item.totalCost);
    });
    return [...totals.entries()].sort(([left], [right]) => left.localeCompare(right));
  }, [groups]);
  const totalResources = useMemo(
    () => groups.reduce((sum, group) => sum + group.resourceCount, 0),
    [groups]
  );
  const hiddenByMinCost = 0;
  const totalGroups = response?.totalCount ?? groups.length;
  const totalPages = Math.max(1, Math.ceil(totalGroups / GROUP_PAGE_SIZE));

  return (
    <div className="page">
      <div className="page-header">
        <h2>Recursos e custos</h2>
        <span className="page-date">
          {response ? `${totalGroups} resource group(s) · página ${page} de ${totalPages}` : '...'}
        </span>
      </div>

      <div className="alert">
        Custos observados no snapshot, somados por <strong>resource group</strong>; os recursos de um
        grupo são buscados só quando você expande. Fonte: <strong>{sourceLabel(response?.source)}</strong>
        {response?.dataAsOf ? ` · dados até ${response.dataAsOf.slice(0, 10)}` : ''}
        {response?.requestedDate && response.dataAsOf?.slice(0, 10) !== response.requestedDate.slice(0, 10)
          ? ` · último snapshot válido para a data solicitada (${response.requestedDate.slice(0, 10)})`
          : ''}.
        {' '}A utilização técnica vem do último snapshot local; ausência de métrica não é tratada como zero.
        {response && (
          <>
            {' '}Cobertura desta grade: <strong>{snapshotCoverage.subscriptionsWithData}</strong> assinatura(s) com snapshot de recurso
            {snapshotCoverage.dates.length > 0 ? ` (${snapshotCoverage.dates[0]} a ${snapshotCoverage.dates.at(-1)})` : ''}.
          </>
        )}
      </div>

      <div className="filters filters--compact">
        <div className="filter-group">
          <label htmlFor="resources-date">Data consolidada</label>
          <input
            id="resources-date"
            type="date"
            value={date}
            max={dateKey(0)}
            onChange={(event) => { setDate(event.target.value); setExpandedGroup(null); }}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="resources-subscription">Subscription</label>
          <input
            id="resources-subscription"
            list="resources-subscriptions"
            value={subscriptionFilter}
            placeholder="Todas"
            onChange={(event) => { setSubscriptionFilter(event.target.value.trim()); setExpandedGroup(null); }}
          />
          <datalist id="resources-subscriptions">
            {subscriptions.map((subscription) => <option key={subscription.subscriptionId} value={subscription.subscriptionId}>{subscription.subscriptionName}</option>)}
          </datalist>
        </div>
        <div className="filter-group">
          <label htmlFor="resources-service">Serviço</label>
          <select id="resources-service" value={serviceFilter} onChange={(event) => { setServiceFilter(event.target.value); setExpandedGroup(null); }}>
            <option value="">Todos</option>
            {services.map((service) => <option key={service} value={service}>{service}</option>)}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="resources-search">Resource group</label>
          <input
            id="resources-search"
            value={searchText}
            placeholder="Parte do nome do grupo"
            onChange={(event) => { setSearchText(event.target.value); setExpandedGroup(null); }}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="resources-min-cost">Custo mínimo</label>
          <input
            id="resources-min-cost"
            inputMode="decimal"
            value={minCost}
            placeholder="Ex.: 10"
            title="Esconde grupos abaixo deste valor. O filtro é da tela: o total dos cards passa a somar só o que ficou visível."
            onChange={(event) => { setMinCost(event.target.value); setExpandedGroup(null); }}
          />
        </div>
        <div className="filter-group recommendations-column-actions">
          <label>&nbsp;</label>
          <button
            className="btn btn--secondary"
            disabled={!subscriptionFilter && !serviceFilter && !searchText && !minCost}
            onClick={() => {
              setSubscriptionFilter('');
              setServiceFilter('');
              setSearchText('');
              setMinCost('');
              setExpandedGroup(null);
            }}
          >
            Limpar filtros
          </button>
        </div>
      </div>

      {loading && <div className="loading">Carregando custos por resource group...</div>}
      {error && (
        <div className="alert alert--warning">
          Não foi possível carregar os custos por recurso. <button type="button" className="btn btn--secondary" onClick={refetch}>Tentar novamente</button>
          <br />
          <small>{error}</small>
        </div>
      )}

      {!loading && !error && response && (
        <>
          <div className="cards-grid">
            <Card
              title="Resource groups"
              value={totalGroups.toLocaleString('pt-BR')}
              subtitle={hiddenByMinCost > 0
                ? `${hiddenByMinCost} abaixo do custo mínimo, fora dos totais`
                : `${groups.length} exibidos nesta página · dados de ${response.dataAsOf?.slice(0, 10) ?? date}`}
              icon={<Boxes size={20} />}
            />
            <Card title="Recursos com custo" value={totalResources.toLocaleString('pt-BR')} subtitle="Somados nos grupos exibidos" icon={<Database size={20} />} />
            {totalsByCurrency.map(([currency, total]) => (
              <Card key={currency} title={`Custo no snapshot (${currency})`} value={formatCurrency(total, currency)} subtitle="Soma dos grupos exibidos" icon={<Coins size={20} />} variant="success" />
            ))}
          </div>

          <div className="section">
            <div className="section-header">
              <h3>Custos por resource group</h3>
              {totalPages > 1 && (
                <div className="recommendations-column-actions">
                  <button className="btn btn--secondary" type="button" disabled={page === 1} onClick={() => setPage(page - 1)}>Anterior</button>
                  <span className="text-muted">Página {page} de {totalPages}</span>
                  <button className="btn btn--secondary" type="button" disabled={page === totalPages} onClick={() => setPage(page + 1)}>Próxima</button>
                </div>
              )}
            </div>
            <div className="table-container">
              <table className="dashboard-table">
                <thead>
                  <tr>
                    <th style={{ width: '48px' }}></th>
                    <th {...groupSort.sortableHeader('resourceGroup')}>Resource Group{groupSort.indicator('resourceGroup')}</th>
                    <th {...groupSort.sortableHeader('subscriptionId')}>Subscription{groupSort.indicator('subscriptionId')}</th>
                    <th {...groupSort.sortableHeader('topService')}>Serviço principal{groupSort.indicator('topService')}</th>
                    <th {...groupSort.sortableHeader('resourceCount')}>Recursos{groupSort.indicator('resourceCount')}</th>
                    <th {...groupSort.sortableHeader('totalCost')}>Custo no snapshot{groupSort.indicator('totalCost')}</th>
                    <th {...groupSort.sortableHeader('dataAsOf')}>Fonte / dados até{groupSort.indicator('dataAsOf')}</th>
                  </tr>
                </thead>
                <tbody>
                  {groupSort.sorted.map((group) => {
                    const key = groupKey(group);
                    const isExpanded = expandedGroup === key;
                    return (
                      <Fragment key={key}>
                        <tr>
                          <td>
                            <button
                              type="button"
                              className="anomaly-expand-btn"
                              aria-expanded={isExpanded}
                              title={isExpanded ? 'Ocultar recursos' : 'Ver recursos do grupo'}
                              onClick={() => setExpandedGroup(isExpanded ? null : key)}
                            >
                              {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                            </button>
                          </td>
                          <td><strong>{group.resourceGroup}</strong></td>
                          <td title={group.subscriptionId}>{group.subscriptionId || '—'}</td>
                          <td>
                            {group.topService || '—'}
                            {group.serviceCount > 1 && (
                              <span className="cost-breakdown-meta">+{group.serviceCount - 1} outro(s) serviço(s)</span>
                            )}
                          </td>
                          <td>{group.resourceCount.toLocaleString('pt-BR')}</td>
                          <td>{formatCurrency(group.totalCost, group.currency)}</td>
                          <td>
                            {sourceLabel(response.source)}
                            <span className="cost-breakdown-meta">
                              {group.dataAsOf?.slice(0, 10) || response.dataAsOf?.slice(0, 10) || '—'}
                              {group.isStale ? ' · último válido' : ''}
                            </span>
                          </td>
                        </tr>
                        {isExpanded && (
                          <tr className="anomaly-detail-row">
                            <td colSpan={7}>
                              <ResourceGroupDetail
                                date={date}
                                group={group}
                                service={serviceFilter}
                                fallbackSource={response.source}
                              />
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    );
                  })}
                  {groups.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center">
                        Nenhum resource group com custo para os filtros selecionados.
                        {hiddenByMinCost > 0 && ` ${hiddenByMinCost} grupo(s) ficaram abaixo do custo mínimo.`}
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

function ResourceGroupDetail({
  date,
  group,
  service,
  fallbackSource,
}: {
  date: string;
  group: CostByResourceGroupRow;
  service: string;
  fallbackSource?: string | null;
}) {
  const [page, setPage] = useState(1);
  useEffect(() => {
    setPage(1);
  }, [date, group.subscriptionId, group.resourceGroup, service]);
  const { data, loading, error } = useFetch(
    () => api.getCostByResource(date, {
      subscriptionId: group.subscriptionId || undefined,
      resourceGroup: group.resourceGroup,
      service: service || undefined,
      page,
      pageSize: RESOURCE_PAGE_SIZE,
    }),
    [date, group.subscriptionId, group.resourceGroup, service, page]
  );

  // Sinais técnicos só são buscados junto do detalhe: são caros e não mudam o custo observado.
  const { data: recommendationSnapshot } = useFetch(
    () => api.getRecommendations(data?.dataAsOf?.slice(0, 10) || date).catch(() => null),
    [data?.dataAsOf, date]
  );

  // Declarado antes dos retornos antecipados: hook não pode ficar atrás de um `if`.
  const resourceSort = useTableSort<CostByResourceRow, ResourceSortKey>(
    data?.items ?? [],
    {
      label: (resource) => resource.label || resource.resourceId,
      serviceName: (resource) => resource.serviceName,
      resourceLocation: (resource) => resource.resourceLocation,
      totalCost: (resource) => resource.totalCost,
      count: (resource) => resource.count,
    },
    { key: 'totalCost', direction: 'desc' },
    MONEY_FIRST_RESOURCE_COLUMNS
  );

  const recommendationByResource = useMemo(() => {
    const index = new Map<string, RecommendationItem>();
    recommendationSnapshot?.recommendations.forEach((item) => {
      const key = resourceKey(item.costMatchedResourceId || item.resourceId);
      if (key && !index.has(key)) index.set(key, item);
    });
    return index;
  }, [recommendationSnapshot]);

  if (loading) return <div className="loading">Carregando recursos de {group.resourceGroup}...</div>;
  if (error) {
    return (
      <div className="alert alert--warning">
        Não foi possível carregar os recursos deste grupo.
        <br />
        <small>{error}</small>
      </div>
    );
  }

  const resources = data?.items ?? [];
  const totalResources = data?.totalCount ?? resources.length;
  const totalPages = Math.max(1, Math.ceil(totalResources / RESOURCE_PAGE_SIZE));

  return (
    <div className="table-container">
      {totalPages > 1 && (
        <div className="recommendations-column-actions">
          <button className="btn btn--secondary" type="button" disabled={page === 1} onClick={() => setPage(page - 1)}>Anterior</button>
          <span className="text-muted">{totalResources} recursos · página {page} de {totalPages}</span>
          <button className="btn btn--secondary" type="button" disabled={page === totalPages} onClick={() => setPage(page + 1)}>Próxima</button>
        </div>
      )}
      <table className="dashboard-table">
        <thead>
          <tr>
            <th {...resourceSort.sortableHeader('label')}>Recurso{resourceSort.indicator('label')}</th>
            <th {...resourceSort.sortableHeader('serviceName')}>Serviço{resourceSort.indicator('serviceName')}</th>
            <th {...resourceSort.sortableHeader('resourceLocation')}>Localização{resourceSort.indicator('resourceLocation')}</th>
            <th {...resourceSort.sortableHeader('totalCost')}>Custo no snapshot{resourceSort.indicator('totalCost')}</th>
            <th>Utilização observada</th>
            <th {...resourceSort.sortableHeader('count')}>Itens de custo{resourceSort.indicator('count')}</th>
          </tr>
        </thead>
        <tbody>
          {resourceSort.sorted.map((resource: CostByResourceRow) => {
            const usage = usageLabel(recommendationByResource.get(resourceKey(resource.resourceId)));
            return (
              <tr key={`${resource.resourceId}-${resource.currency}`}>
                <td title={resource.resourceId}>
                  {resource.label || resource.resourceId}
                  <span className="cost-breakdown-meta">{resource.resourceType || 'Tipo não informado'}</span>
                </td>
                <td>{resource.serviceName || '—'}</td>
                <td>{resource.resourceLocation || '—'}</td>
                <td>{formatCurrency(resource.totalCost, resource.currency)}</td>
                <td>
                  {usage.label}
                  {usage.observedAt && <span className="cost-breakdown-meta">{usage.observedAt.slice(0, 10)}</span>}
                </td>
                <td>{resource.count.toLocaleString('pt-BR')}</td>
              </tr>
            );
          })}
          {resources.length === 0 && (
            <tr><td colSpan={6} className="text-center">Nenhum recurso com custo neste grupo.</td></tr>
          )}
        </tbody>
      </table>
      {(data?.totalCount ?? 0) > resources.length && (
        <p className="text-muted">
          Exibindo {resources.length} de {data?.totalCount} recursos do grupo. Refine o filtro de serviço
          para ver o restante.
        </p>
      )}
      {fallbackSource && resources.some((resource) => resource.isStale) && (
        <p className="text-muted">Parte destes recursos vem do último snapshot válido, não da data solicitada.</p>
      )}
    </div>
  );
}
