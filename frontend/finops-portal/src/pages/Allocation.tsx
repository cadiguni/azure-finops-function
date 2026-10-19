import { Fragment, useMemo, useState } from 'react';
import { ChevronDown, ChevronRight, Coins, PieChart, SlidersHorizontal, AlertTriangle, Search } from 'lucide-react';
import Card from '../components/Card';
import { useFetch } from '../hooks/useFetch';
import { useTableSort } from '../hooks/useTableSort';
import { useAccess } from '../auth/AccessContext';
import { api } from '../services/api';
import type {
  CostAllocationBreakdown,
  CostAllocationGroup,
  CostAllocationResourceGroup,
  CostAllocationResourceItem,
  CostAllocationRule,
  CostAllocationRuleType,
  OwnershipConfig,
} from '../types/api';

const PRODUCTION_SCOPE = 'production';
const ALL_SCOPE = 'all';
const UNASSIGNED_KEY = '__unassigned__';

const RULE_TYPE_LABELS: Record<CostAllocationRuleType, string> = {
  ResourceGroup: 'Resource group',
  NamePrefix: 'Prefixo de nome',
  Service: 'Serviço',
  TagAlias: 'Equivalência de tag',
};

const RULE_TYPE_HINTS: Record<CostAllocationRuleType, string> = {
  ResourceGroup: 'Nome exato do resource group (ex.: rg-erp-prod)',
  NamePrefix: 'Prefixo do nome do recurso (ex.: sql-erp-)',
  Service: 'Nome do serviço no Cost Management (ex.: Virtual Machines)',
  TagAlias: 'Valor da tag que deve ser lido como outro centro de custo (ex.: ti-infra)',
};

const SOURCE_LABELS: Record<string, string> = {
  ResourceGroupRule: 'Ajuste por resource group',
  NamePrefixRule: 'Ajuste por prefixo',
  ServiceRule: 'Ajuste por serviço',
  TagAlias: 'Tag normalizada',
  Tag: 'Tag',
  Unassigned: 'Sem atribuição',
};

type PeriodOption = 'current-month' | 'last-30' | 'previous-month';
type OriginFilter = 'all' | 'tag' | 'manual' | 'divergent';
type CenterSortKey = 'costCenter' | 'resourceGroups' | 'resourceCount' | 'percent' | 'manualCost' | 'totalCost';
type ResourceGroupSortKey = 'resourceGroup' | 'resourceCount' | 'totalCost';
type ResourceSortKey = 'label' | 'serviceName' | 'source' | 'totalCost';

const CENTER_MONEY_COLUMNS: CenterSortKey[] = ['resourceGroups', 'resourceCount', 'percent', 'manualCost', 'totalCost'];
const RESOURCE_GROUP_MONEY_COLUMNS: ResourceGroupSortKey[] = ['resourceCount', 'totalCost'];
const RESOURCE_MONEY_COLUMNS: ResourceSortKey[] = ['totalCost'];

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

function toKey(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

// O Cost Management só consolida com dois dias de atraso; o período termina antes de hoje.
function consolidatedEnd(): Date {
  const date = new Date();
  date.setDate(date.getDate() - 2);
  return date;
}

function resolvePeriod(option: PeriodOption): { from: string; to: string } {
  const end = consolidatedEnd();
  if (option === 'last-30') {
    const start = new Date(end);
    start.setDate(start.getDate() - 29);
    return { from: toKey(start), to: toKey(end) };
  }

  if (option === 'previous-month') {
    const start = new Date(end.getFullYear(), end.getMonth() - 1, 1);
    const finish = new Date(end.getFullYear(), end.getMonth(), 0);
    return { from: toKey(start), to: toKey(finish) };
  }

  return { from: toKey(new Date(end.getFullYear(), end.getMonth(), 1)), to: toKey(end) };
}

function formatCurrency(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat('pt-BR', { style: 'currency', currency }).format(value);
  } catch {
    return `${currency} ${value.toLocaleString('pt-BR', { maximumFractionDigits: 2 })}`;
  }
}

export default function Allocation() {
  const { isAdmin } = useAccess();
  const [period, setPeriod] = useState<PeriodOption>('current-month');
  const [scope, setScope] = useState<string>(PRODUCTION_SCOPE);
  const [expandedCenter, setExpandedCenter] = useState<string | null>(null);
  const [expandedGroup, setExpandedGroup] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [origin, setOrigin] = useState<OriginFilter>('all');
  const [minCost, setMinCost] = useState('');
  const [showRules, setShowRules] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionInfo, setActionInfo] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [draft, setDraft] = useState<{
    type: CostAllocationRuleType;
    match: string;
    costCenter: string;
    scoped: boolean;
    note: string;
  }>({ type: 'ResourceGroup', match: '', costCenter: '', scoped: true, note: '' });

  const range = useMemo(() => resolvePeriod(period), [period]);
  const subscriptionParam = scope === PRODUCTION_SCOPE ? undefined : scope;

  const { data: breakdown, loading, error, refetch } = useFetch<CostAllocationBreakdown>(
    () => api.getCostAllocation(range.from, range.to, subscriptionParam),
    [range.from, range.to, scope]
  );

  const { data: rulesConfig, refetch: refetchRules } = useFetch(
    () => api.getCostAllocationRules().catch(() => null),
    []
  );

  const { data: ownership } = useFetch(() => api.getOwnershipTeams().catch(() => null), []);

  const subscriptionOptions = useMemo(() => {
    const config = ownership as OwnershipConfig | null;
    return (config?.teams ?? [])
      .flatMap((team) => team.subscriptions)
      .sort((left, right) => left.subscriptionName.localeCompare(right.subscriptionName));
  }, [ownership]);

  const currency = breakdown?.currency ?? 'BRL';
  const rules = rulesConfig?.rules ?? [];
  const query = search.trim().toLowerCase();

  // A busca filtra recurso a recurso e recalcula os totais do que sobrou: quem procura um recurso
  // quer saber onde ele caiu, e um total que não corresponde ao que está na tela confundiria.
  const minCostValue = Number.parseFloat(minCost.replace(',', '.'));
  const hasMinCost = Number.isFinite(minCostValue) && minCostValue > 0;
  const matchesOrigin = (resource: CostAllocationResourceItem): boolean => {
    if (origin === 'all') return true;
    if (origin === 'tag') return resource.source === 'Tag';
    if (origin === 'divergent') return resource.overridesTag;
    return resource.source !== 'Tag';
  };

  const filterGroup = (group: CostAllocationGroup): CostAllocationGroup | null => {
    if (!query && origin === 'all' && !hasMinCost) return group;
    const resourceGroups = group.resourceGroups
      .map((resourceGroup) => ({
        ...resourceGroup,
        resources: resourceGroup.resources.filter((resource) =>
          matchesOrigin(resource)
          && (!query
            || [resource.label, resource.resourceId, resource.resourceGroup, resource.serviceName, resource.tagValue ?? '']
              .some((field) => field.toLowerCase().includes(query)))
        ),
      }))
      .filter((resourceGroup) => resourceGroup.resources.length > 0)
      .map((resourceGroup) => ({
        ...resourceGroup,
        totalCost: resourceGroup.resources.reduce((sum, resource) => sum + resource.totalCost, 0),
        resourceCount: resourceGroup.resources.length,
      }));
    if (resourceGroups.length === 0) return null;
    const totalCost = resourceGroups.reduce((sum, resourceGroup) => sum + resourceGroup.totalCost, 0);
    if (hasMinCost && totalCost < minCostValue) return null;
    return {
      ...group,
      resourceGroups,
      totalCost,
      resourceCount: resourceGroups.reduce((sum, resourceGroup) => sum + resourceGroup.resourceCount, 0),
      manualCost: resourceGroups
        .flatMap((resourceGroup) => resourceGroup.resources)
        .filter((resource) => resource.source !== 'Tag')
        .reduce((sum, resource) => sum + resource.totalCost, 0),
    };
  };

  const groups: CostAllocationGroup[] = useMemo(
    () => (breakdown?.costCenters ?? []).map(filterGroup).filter((group): group is CostAllocationGroup => group != null),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [breakdown, query, origin, minCost]
  );
  const unassigned = useMemo(
    () => (breakdown?.unassigned ? filterGroup(breakdown.unassigned) : null),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [breakdown, query, origin, minCost]
  );
  const hasUnassigned = (unassigned?.resourceCount ?? 0) > 0;
  const hasActiveFilter = Boolean(query) || origin !== 'all' || hasMinCost;
  const hiddenByFilters = hasActiveFilter
    ? (breakdown?.costCenters.length ?? 0) + 1 - groups.length - (hasUnassigned ? 1 : 0)
    : 0;
  const tableContext = {
    currency,
    isAdmin,
    showSubscription: scope === ALL_SCOPE,
    expandedGroup,
    onToggleGroup: setExpandedGroup,
    onAssign: prefillFromResource,
  };
  const centerSort = useTableSort<CostAllocationGroup, CenterSortKey>(
    groups,
    {
      costCenter: (group) => group.costCenter,
      resourceGroups: (group) => group.resourceGroups.length,
      resourceCount: (group) => group.resourceCount,
      percent: (group) => group.percent,
      manualCost: (group) => group.manualCost,
      totalCost: (group) => group.totalCost,
    },
    { key: 'totalCost', direction: 'desc' },
    CENTER_MONEY_COLUMNS
  );

  // O escopo do rateio é a assinatura consultada: uma regra criada daqui nasce com ela.
  const scopedSubscriptionId = breakdown?.subscriptionId ?? '';

  async function handleCreateRule() {
    if (!draft.match.trim() || !draft.costCenter.trim()) {
      setActionError('Informe o valor a comparar e o centro de custo.');
      return;
    }

    setSaving(true);
    setActionError(null);
    setActionInfo(null);
    try {
      await api.createCostAllocationRule({
        type: draft.type,
        match: draft.match.trim(),
        costCenter: draft.costCenter.trim(),
        subscriptionId: draft.scoped ? scopedSubscriptionId : '',
        note: draft.note.trim() || undefined,
      });
      setDraft({ type: draft.type, match: '', costCenter: '', scoped: draft.scoped, note: '' });
      setActionInfo('Ajuste salvo. O rateio do período foi recalculado.');
      refetchRules();
      refetch();
    } catch (exception) {
      setActionError(exception instanceof Error ? exception.message : 'Falha ao salvar o ajuste.');
    } finally {
      setSaving(false);
    }
  }

  async function handleToggleRule(rule: CostAllocationRule) {
    setSaving(true);
    setActionError(null);
    try {
      await api.updateCostAllocationRule(rule.id, { enabled: !rule.enabled });
      refetchRules();
      refetch();
    } catch (exception) {
      setActionError(exception instanceof Error ? exception.message : 'Falha ao atualizar o ajuste.');
    } finally {
      setSaving(false);
    }
  }

  async function handleDeleteRule(rule: CostAllocationRule) {
    setSaving(true);
    setActionError(null);
    try {
      await api.deleteCostAllocationRule(rule.id);
      setActionInfo(`Ajuste de '${rule.match}' removido.`);
      refetchRules();
      refetch();
    } catch (exception) {
      setActionError(exception instanceof Error ? exception.message : 'Falha ao remover o ajuste.');
    } finally {
      setSaving(false);
    }
  }

  function prefillFromResource(resource: CostAllocationResourceItem) {
    setShowRules(true);
    setActionInfo(null);
    setActionError(null);
    setDraft({
      type: 'ResourceGroup',
      match: resource.resourceGroup,
      costCenter: '',
      scoped: true,
      note: `Definido a partir de ${resource.label}`,
    });
  }

  return (
    <div className="page">
      <div className="page-header">
        <h2>Rateio por centro de custo</h2>
        <span className="page-date">
          {breakdown ? `${breakdown.from} a ${breakdown.to}` : '...'}
        </span>
      </div>

      <div className="alert">
        O rateio parte da tag <strong>{breakdown?.tagName ?? 'CentrodeCusto'}</strong> e aplica os
        ajustes manuais por cima dela — as tags do ambiente estão defasadas e corrigi-las no Azure
        é mais lento que o fechamento. O total considerado é o <strong>custo por recurso</strong> do
        período, que é menor que a fatura da assinatura: linhas sem recurso identificado não entram.
        Custo sem tag e sem ajuste fica em <strong>Não atribuído</strong>, nunca distribuído entre os
        centros de custo.
        {breakdown && breakdown.daysWithoutData > 0 && (
          <> {breakdown.daysWithoutData} dia(s) do período sem snapshot de custo.</>
        )}
      </div>

      <div className="filters filters--compact">
        <div className="filter-group">
          <label htmlFor="allocation-period">Período</label>
          <select
            id="allocation-period"
            value={period}
            onChange={(event) => setPeriod(event.target.value as PeriodOption)}
          >
            <option value="current-month">Mês atual</option>
            <option value="previous-month">Mês anterior</option>
            <option value="last-30">Últimos 30 dias</option>
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="allocation-scope">Assinatura</label>
          <select
            id="allocation-scope"
            value={scope}
            onChange={(event) => setScope(event.target.value)}
          >
            <option value={PRODUCTION_SCOPE}>Produção (padrão)</option>
            <option value={ALL_SCOPE}>Todas as assinaturas</option>
            {subscriptionOptions.map((subscription) => (
              <option key={subscription.subscriptionId} value={subscription.subscriptionId}>
                {subscription.subscriptionName || subscription.subscriptionId}
              </option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="allocation-search">Buscar recurso</label>
          <input
            id="allocation-search"
            type="search"
            value={search}
            placeholder="Nome, resource group ou valor da tag"
            onChange={(event) => setSearch(event.target.value)}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="allocation-origin">Origem</label>
          <select
            id="allocation-origin"
            value={origin}
            onChange={(event) => setOrigin(event.target.value as OriginFilter)}
          >
            <option value="all">Todas</option>
            <option value="tag">Só o que veio da tag</option>
            <option value="manual">Só o que veio de ajuste</option>
            <option value="divergent">Só onde a tag diverge do ajuste</option>
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="allocation-min-cost">Custo mínimo</label>
          <input
            id="allocation-min-cost"
            inputMode="decimal"
            value={minCost}
            placeholder="Ex.: 50"
            title="Esconde centros de custo abaixo deste valor. Os totais passam a somar só o que ficou visível."
            onChange={(event) => setMinCost(event.target.value)}
          />
        </div>
        <div className="filter-group recommendations-column-actions">
          <label>&nbsp;</label>
          <button
            className="btn btn--secondary"
            disabled={!hasActiveFilter}
            onClick={() => { setSearch(''); setOrigin('all'); setMinCost(''); }}
          >
            Limpar filtros
          </button>
        </div>
        {isAdmin && (
          <div className="filter-group recommendations-column-actions">
            <label>&nbsp;</label>
            <button className="btn btn--secondary" onClick={() => setShowRules((value) => !value)}>
              <SlidersHorizontal size={16} /> {showRules ? 'Ocultar ajustes' : `Ajustes manuais (${rules.length})`}
            </button>
          </div>
        )}
      </div>

      {loading && <div className="loading">Montando o rateio...</div>}
      {error && (
        <div className="alert alert--warning">
          Não foi possível carregar o rateio.
          <br />
          <small>{error}</small>
        </div>
      )}

      {!loading && !error && breakdown && (
        <>
          <div className="cards-grid">
            <Card
              title="Custo por recurso no período"
              value={formatCurrency(breakdown.totalCost, currency)}
              subtitle={`${groups.length} centro(s) de custo · ${breakdown.subscriptionIds.length} assinatura(s)`}
              icon={<Coins size={20} />}
            />
            <Card
              title="Atribuído"
              value={formatCurrency(breakdown.assignedCost, currency)}
              subtitle={
                breakdown.coveragePercent == null
                  ? 'sem custo no período'
                  : `${breakdown.coveragePercent.toFixed(1)}% do custo por recurso`
              }
              icon={<PieChart size={20} />}
              variant="success"
            />
            <Card
              title="Não atribuído"
              value={formatCurrency(breakdown.unassignedCost, currency)}
              subtitle={`${unassigned?.resourceCount ?? 0} recurso(s) sem tag e sem ajuste`}
              icon={<AlertTriangle size={20} />}
              variant={hasUnassigned ? 'warning' : 'success'}
            />
            <Card
              title="Vindo de ajuste manual"
              value={formatCurrency(breakdown.manualCost, currency)}
              subtitle={`${breakdown.rulesApplied} regra(s) aplicada(s) no período`}
              icon={<SlidersHorizontal size={20} />}
            />
          </div>

          {isAdmin && showRules && (
            <div className="section">
              <div className="section-header">
                <h3>Ajustes manuais</h3>
                <span className="text-muted">
                  Precedência: resource group &gt; prefixo de nome &gt; serviço &gt; tag.
                </span>
              </div>
              {actionError && <div className="alert alert--warning">{actionError}</div>}
              {actionInfo && <div className="alert">{actionInfo}</div>}

              <div className="filters filters--compact">
                <div className="filter-group">
                  <label htmlFor="rule-type">Tipo</label>
                  <select
                    id="rule-type"
                    value={draft.type}
                    onChange={(event) =>
                      setDraft({ ...draft, type: event.target.value as CostAllocationRuleType })
                    }
                  >
                    {(Object.keys(RULE_TYPE_LABELS) as CostAllocationRuleType[]).map((type) => (
                      <option key={type} value={type}>{RULE_TYPE_LABELS[type]}</option>
                    ))}
                  </select>
                </div>
                <div className="filter-group">
                  <label htmlFor="rule-match">Valor comparado</label>
                  <input
                    id="rule-match"
                    value={draft.match}
                    placeholder={RULE_TYPE_HINTS[draft.type]}
                    onChange={(event) => setDraft({ ...draft, match: event.target.value })}
                  />
                </div>
                <div className="filter-group">
                  <label htmlFor="rule-cost-center">Centro de custo</label>
                  <input
                    id="rule-cost-center"
                    value={draft.costCenter}
                    list="allocation-cost-centers"
                    placeholder="Ex.: Infraestrutura"
                    onChange={(event) => setDraft({ ...draft, costCenter: event.target.value })}
                  />
                  <datalist id="allocation-cost-centers">
                    {groups.map((group) => (
                      <option key={group.costCenter} value={group.costCenter} />
                    ))}
                  </datalist>
                </div>
                <div className="filter-group">
                  <label htmlFor="rule-note">Motivo</label>
                  <input
                    id="rule-note"
                    value={draft.note}
                    placeholder="Por que a tag não vale aqui"
                    onChange={(event) => setDraft({ ...draft, note: event.target.value })}
                  />
                </div>
                <div className="filter-group recommendations-column-actions">
                  <label>&nbsp;</label>
                  <button className="btn" disabled={saving} onClick={() => void handleCreateRule()}>
                    {saving ? 'Salvando...' : 'Adicionar ajuste'}
                  </button>
                </div>
              </div>
              <label className="text-muted">
                <input
                  type="checkbox"
                  checked={draft.scoped}
                  disabled={!scopedSubscriptionId}
                  onChange={(event) => setDraft({ ...draft, scoped: event.target.checked })}
                />{' '}
                Aplicar somente à assinatura consultada
                {scopedSubscriptionId ? ` (${scopedSubscriptionId})` : ' — indisponível no modo "todas"'}
              </label>

              {rules.length === 0 ? (
                <p className="cost-breakdown-empty">
                  Nenhum ajuste cadastrado: o rateio está saindo inteiro das tags.
                </p>
              ) : (
                <div className="table-container">
                  <table className="dashboard-table">
                    <thead>
                      <tr>
                        <th>Tipo</th>
                        <th>Valor comparado</th>
                        <th>Centro de custo</th>
                        <th>Escopo</th>
                        <th>Motivo</th>
                        <th>Atualizado</th>
                        <th>Ações</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rules.map((rule) => (
                        <tr key={rule.id} className={rule.enabled ? '' : 'row--muted'}>
                          <td>{RULE_TYPE_LABELS[rule.type] ?? rule.type}</td>
                          <td className="font-mono">{rule.match}</td>
                          <td><strong>{rule.costCenter}</strong></td>
                          <td title={rule.subscriptionId || 'Todas as assinaturas'}>
                            {rule.subscriptionId ? `${rule.subscriptionId.slice(0, 8)}...` : 'Todas'}
                          </td>
                          <td>{rule.note || '—'}</td>
                          <td title={`por ${rule.updatedBy}`}>{rule.updatedAt?.slice(0, 10) ?? '—'}</td>
                          <td>
                            <button
                              className="btn btn--secondary"
                              disabled={saving}
                              onClick={() => void handleToggleRule(rule)}
                            >
                              {rule.enabled ? 'Desativar' : 'Ativar'}
                            </button>{' '}
                            <button
                              className="btn btn--secondary"
                              disabled={saving}
                              onClick={() => void handleDeleteRule(rule)}
                            >
                              Remover
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}

          <div className="section">
            <div className="section-header">
              <h3>Por centro de custo</h3>
              {hasActiveFilter && (
                <span className="text-muted">
                  <Search size={14} /> Filtro ativo: os totais desta tabela somam só os recursos que
                  passaram pelo filtro, não o período inteiro
                  {hiddenByFilters > 0 ? ` · ${hiddenByFilters} grupo(s) ocultos` : ''}
                </span>
              )}
            </div>
            {groups.length === 0 && !hasUnassigned ? (
              <p className="cost-breakdown-empty">
                {hasActiveFilter
                  ? 'Nenhum recurso corresponde aos filtros no período e na assinatura selecionados.'
                  : 'Sem custo por recurso no período selecionado.'}
              </p>
            ) : (
              <div className="table-container">
                <table className="dashboard-table">
                  <thead>
                    <tr>
                      <th style={{ width: '48px' }}></th>
                      <th {...centerSort.sortableHeader('costCenter')}>Centro de custo{centerSort.indicator('costCenter')}</th>
                      <th {...centerSort.sortableHeader('resourceGroups')}>Resource groups{centerSort.indicator('resourceGroups')}</th>
                      <th {...centerSort.sortableHeader('resourceCount')}>Recursos{centerSort.indicator('resourceCount')}</th>
                      <th {...centerSort.sortableHeader('percent')}>Participação{centerSort.indicator('percent')}</th>
                      <th {...centerSort.sortableHeader('manualCost')}>Vindo de ajuste{centerSort.indicator('manualCost')}</th>
                      <th {...centerSort.sortableHeader('totalCost')}>Total no período{centerSort.indicator('totalCost')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {centerSort.sorted.map((group) => {
                      const isExpanded = expandedCenter === group.costCenter;
                      return (
                        <Fragment key={group.costCenter}>
                          <tr>
                            <td>
                              <button
                                type="button"
                                className="anomaly-expand-btn"
                                aria-expanded={isExpanded}
                                title={isExpanded ? 'Ocultar resource groups' : 'Ver resource groups'}
                                onClick={() => setExpandedCenter(isExpanded ? null : group.costCenter)}
                              >
                                {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                              </button>
                            </td>
                            <td>
                              <strong>{group.costCenter}</strong>
                              {group.variants.length > 1 && (
                                <div className="text-muted" title={group.variants.join(' · ')}>
                                  {group.variants.length} grafias diferentes na tag
                                </div>
                              )}
                            </td>
                            <td>{group.resourceGroups.length}</td>
                            <td>{group.resourceCount}</td>
                            <td>{group.percent.toFixed(1)}%</td>
                            <td>
                              {group.manualCost > 0 ? formatCurrency(group.manualCost, currency) : '—'}
                            </td>
                            <td>{formatCurrency(group.totalCost, currency)}</td>
                          </tr>
                          {isExpanded && (
                            <tr className="anomaly-detail-row">
                              <td colSpan={7}>
                                <ResourceGroupTable group={group} allowAssign={false} context={tableContext} />
                              </td>
                            </tr>
                          )}
                        </Fragment>
                      );
                    })}

                    {hasUnassigned && unassigned && (
                      <Fragment>
                        <tr className="row--attention">
                          <td>
                            <button
                              type="button"
                              className="anomaly-expand-btn"
                              aria-expanded={expandedCenter === UNASSIGNED_KEY}
                              title="Ver resource groups sem atribuição"
                              onClick={() =>
                                setExpandedCenter(expandedCenter === UNASSIGNED_KEY ? null : UNASSIGNED_KEY)
                              }
                            >
                              {expandedCenter === UNASSIGNED_KEY ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                            </button>
                          </td>
                          <td>
                            <strong>Não atribuído</strong>
                            <div className="text-muted">sem tag e sem ajuste manual</div>
                          </td>
                          <td>{unassigned.resourceGroups.length}</td>
                          <td>{unassigned.resourceCount}</td>
                          <td>{unassigned.percent.toFixed(1)}%</td>
                          <td>—</td>
                          <td>{formatCurrency(unassigned.totalCost, currency)}</td>
                        </tr>
                        {expandedCenter === UNASSIGNED_KEY && (
                          <tr className="anomaly-detail-row">
                            <td colSpan={7}>
                              <ResourceGroupTable group={unassigned} allowAssign context={tableContext} />
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    )}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}

interface AllocationTableContext {
  currency: string;
  isAdmin: boolean;
  showSubscription: boolean;
  expandedGroup: string | null;
  onToggleGroup: (key: string | null) => void;
  onAssign: (resource: CostAllocationResourceItem) => void;
}

/**
 * Um centro de custo abre nos seus resource groups; os recursos vêm só no segundo nível.
 * Fica no nível do módulo, e não dentro da página: declarado lá dentro, cada render da página
 * criaria um tipo de componente novo e a ordenação escolhida aqui se perderia.
 */
function ResourceGroupTable({
  group,
  allowAssign,
  context,
}: {
  group: CostAllocationGroup;
  allowAssign: boolean;
  context: AllocationTableContext;
}) {
  const sort = useTableSort<CostAllocationResourceGroup, ResourceGroupSortKey>(
    group.resourceGroups,
    {
      resourceGroup: (item) => item.resourceGroup,
      resourceCount: (item) => item.resourceCount,
      totalCost: (item) => item.totalCost,
    },
    { key: 'totalCost', direction: 'desc' },
    RESOURCE_GROUP_MONEY_COLUMNS
  );

  return (
    <div className="table-container">
      <table className="dashboard-table">
        <thead>
          <tr>
            <th style={{ width: '48px' }}></th>
            <th {...sort.sortableHeader('resourceGroup')}>Resource group{sort.indicator('resourceGroup')}</th>
            <th {...sort.sortableHeader('resourceCount')}>Recursos{sort.indicator('resourceCount')}</th>
            <th {...sort.sortableHeader('totalCost')}>Custo no período{sort.indicator('totalCost')}</th>
          </tr>
        </thead>
        <tbody>
          {sort.sorted.map((resourceGroup) => {
            const key = `${group.costCenter}|${resourceGroup.subscriptionId}|${resourceGroup.resourceGroup}`;
            const isOpen = context.expandedGroup === key;
            return (
              <Fragment key={key}>
                <tr>
                  <td>
                    <button
                      type="button"
                      className="anomaly-expand-btn"
                      aria-expanded={isOpen}
                      title={isOpen ? 'Ocultar recursos' : 'Ver recursos'}
                      onClick={() => context.onToggleGroup(isOpen ? null : key)}
                    >
                      {isOpen ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                    </button>
                  </td>
                  <td>
                    <strong>{resourceGroup.resourceGroup}</strong>
                    {context.showSubscription && (
                      <div className="text-muted">{resourceGroup.subscriptionId}</div>
                    )}
                  </td>
                  <td>{resourceGroup.resourceCount}</td>
                  <td>{formatCurrency(resourceGroup.totalCost, context.currency)}</td>
                </tr>
                {isOpen && (
                  <tr className="anomaly-detail-row">
                    <td colSpan={4}>
                      <ResourceTable resourceGroup={resourceGroup} allowAssign={allowAssign} context={context} />
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ResourceTable({
  resourceGroup,
  allowAssign,
  context,
}: {
  resourceGroup: CostAllocationResourceGroup;
  allowAssign: boolean;
  context: AllocationTableContext;
}) {
  const sort = useTableSort<CostAllocationResourceItem, ResourceSortKey>(
    resourceGroup.resources,
    {
      label: (resource) => resource.label,
      serviceName: (resource) => resource.serviceName,
      source: (resource) => SOURCE_LABELS[resource.source] ?? resource.source,
      totalCost: (resource) => resource.totalCost,
    },
    { key: 'totalCost', direction: 'desc' },
    RESOURCE_MONEY_COLUMNS
  );
  const canAssign = allowAssign && context.isAdmin;

  return (
    <div className="table-container">
      <table className="dashboard-table">
        <thead>
          <tr>
            <th {...sort.sortableHeader('label')}>Recurso{sort.indicator('label')}</th>
            <th {...sort.sortableHeader('serviceName')}>Serviço{sort.indicator('serviceName')}</th>
            <th {...sort.sortableHeader('source')}>Origem{sort.indicator('source')}</th>
            <th {...sort.sortableHeader('totalCost')}>Custo no período{sort.indicator('totalCost')}</th>
            {canAssign && <th>Ação</th>}
          </tr>
        </thead>
        <tbody>
          {sort.sorted.map((resource) => (
            <tr key={`${resource.subscriptionId}:${resource.resourceId}:${resource.label}`}>
              <td title={resource.resourceId}>
                <strong>{resource.label}</strong>
                {resource.overridesTag && (
                  <div className="text-muted">
                    tag atual: {resource.tagValue} (sobrescrita por ajuste manual)
                  </div>
                )}
              </td>
              <td>{resource.serviceName || '—'}</td>
              <td>
                <span className={resource.source === 'Tag' ? 'badge badge--muted' : 'badge badge--info'}>
                  {SOURCE_LABELS[resource.source] ?? resource.source}
                </span>
              </td>
              <td>{formatCurrency(resource.totalCost, context.currency)}</td>
              {canAssign && (
                <td>
                  <button className="btn btn--secondary" onClick={() => context.onAssign(resource)}>
                    Atribuir
                  </button>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
