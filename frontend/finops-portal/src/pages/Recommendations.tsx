import { useState, useMemo } from 'react';
import StatusBadge from '../components/StatusBadge';
import { api } from '../services/api';
import { useFetch } from '../hooks/useFetch';
import type { RecommendationItem, TeamsResponse } from '../types/api';

function formatCurrency(value: number): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
}

function classifyAction(r: RecommendationItem): string {
  const desc = (r.description ?? '').toLowerCase();
  const type = (r.resourceType ?? '').toLowerCase();
  if (type.includes('disk') && desc.includes('unattached')) return 'Excluir';
  if (type.includes('publicipaddresses')) return 'Excluir';
  if (type.includes('operationalinsights') || type.includes('workspace')) return 'Revisar';
  if (type.includes('serverfarms') || type.includes('web/sites')) return 'Investigar';
  if (type.includes('virtualmachines')) return 'Investigar';
  if (type.includes('storageaccounts')) return 'Investigar';
  return 'Investigar';
}

export default function Recommendations() {
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [filterSub, setFilterSub] = useState('');
  const [filterType, setFilterType] = useState('');
  const [filterAction, setFilterAction] = useState('');
  const [filterPriority, setFilterPriority] = useState('');

  type SortField = 'subscription' | 'resourceGroup' | 'resource' | 'type' | 'action' | 'priority' | 'savings' | 'description';
  const [sortField, setSortField] = useState<SortField>('savings');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');

  const { data: summary, loading, error } = useFetch(
    () => api.getRecommendations(date),
    [date]
  );

  const { data: teamsData } = useFetch(
    () => api.getTeams().catch((): TeamsResponse => ({ teams: [], teamsCount: 0, lastUpdated: '' })),
    []
  );

  const recommendations = summary?.recommendations ?? [];

  // Build subscription id→name map from teams data
  const subNameMap = useMemo(() => {
    const map = new Map<string, string>();
    (teamsData?.teams ?? []).forEach(t => {
      t.subscriptionIds.forEach((id, i) => {
        if (t.subscriptionNames?.[i]) map.set(id, t.subscriptionNames[i]);
      });
    });
    return map;
  }, [teamsData]);

  const uniqueSubs = useMemo(() => [...new Set(recommendations.map(r => r.subscriptionId))], [recommendations]);
  const uniqueTypes = useMemo(() => [...new Set(recommendations.map(r => r.type))], [recommendations]);
  const uniqueActions = useMemo(() => [...new Set(recommendations.map(r => classifyAction(r)))], [recommendations]);

  const filtered = useMemo(() => {
    let items = recommendations;
    if (filterSub) items = items.filter(r => r.subscriptionId === filterSub);
    if (filterType) items = items.filter(r => r.type === filterType);
    if (filterAction) items = items.filter(r => classifyAction(r) === filterAction);
    if (filterPriority) items = items.filter(r => r.priority === filterPriority);

    const priorityOrder: Record<string, number> = { High: 0, Medium: 1, Low: 2 };
    const dir = sortDir === 'asc' ? 1 : -1;

    items = [...items].sort((a, b) => {
      let cmp = 0;
      switch (sortField) {
        case 'subscription': {
          const na = subNameMap.get(a.subscriptionId) || a.subscriptionId;
          const nb = subNameMap.get(b.subscriptionId) || b.subscriptionId;
          cmp = na.localeCompare(nb); break;
        }
        case 'resourceGroup': cmp = a.resourceGroup.localeCompare(b.resourceGroup); break;
        case 'resource': cmp = a.resourceName.localeCompare(b.resourceName); break;
        case 'type': cmp = a.type.localeCompare(b.type); break;
        case 'action': cmp = classifyAction(a).localeCompare(classifyAction(b)); break;
        case 'priority': cmp = (priorityOrder[a.priority] ?? 3) - (priorityOrder[b.priority] ?? 3); break;
        case 'savings': cmp = a.estimatedMonthlySavings - b.estimatedMonthlySavings; break;
        case 'description': cmp = a.description.localeCompare(b.description); break;
      }
      return cmp * dir;
    });
    return items;
  }, [recommendations, filterSub, filterType, filterAction, filterPriority, sortField, sortDir, subNameMap]);

  const totalFilteredSavings = filtered.reduce((sum, r) => sum + r.estimatedMonthlySavings, 0);

  return (
    <div className="page">
      <div className="page-header">
        <h2>Recomendações</h2>
        <span className="page-date">{filtered.length} de {recommendations.length} recursos</span>
      </div>

      <div className="filters">
        <div className="filter-group">
          <label htmlFor="rec-date">Data</label>
          <input id="rec-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </div>
        <div className="filter-group">
          <label htmlFor="rec-sub">Subscription</label>
          <select id="rec-sub" value={filterSub} onChange={(e) => setFilterSub(e.target.value)}>
            <option value="">Todas</option>
            {uniqueSubs.map(s => <option key={s} value={s}>{subNameMap.get(s) || s}</option>)}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-type">Tipo</label>
          <select id="rec-type" value={filterType} onChange={(e) => setFilterType(e.target.value)}>
            <option value="">Todos</option>
            {uniqueTypes.map(t => <option key={t} value={t}>{t}</option>)}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-action">Ação</label>
          <select id="rec-action" value={filterAction} onChange={(e) => setFilterAction(e.target.value)}>
            <option value="">Todas</option>
            {uniqueActions.map(a => <option key={a} value={a}>{a}</option>)}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-priority">Prioridade</label>
          <select id="rec-priority" value={filterPriority} onChange={(e) => setFilterPriority(e.target.value)}>
            <option value="">Todas</option>
            <option value="High">High</option>
            <option value="Medium">Medium</option>
            <option value="Low">Low</option>
          </select>
        </div>
      </div>

      {loading && <div className="loading">Carregando recomendações...</div>}
      {error && (
        <div className="alert alert--warning">
          Não foi possível carregar recomendações.<br /><small>{error}</small>
        </div>
      )}

      {!loading && !error && (
        <>
          <div className="section">
            <div className="table-container">
              <table>
                <thead>
                  <tr>
                    {([
                      ['subscription', 'Subscription'],
                      ['resourceGroup', 'Resource Group'],
                      ['resource', 'Recurso'],
                      ['type', 'Tipo'],
                      ['action', 'Ação'],
                      ['priority', 'Prioridade'],
                      ['savings', 'Economia/mês'],
                      ['description', 'Descrição'],
                    ] as [SortField, string][]).map(([field, label]) => (
                      <th
                        key={field}
                        className="sortable"
                        onClick={() => {
                          if (sortField === field) setSortDir(d => d === 'asc' ? 'desc' : 'asc');
                          else { setSortField(field); setSortDir(field === 'savings' ? 'desc' : 'asc'); }
                        }}
                      >
                        {label} {sortField === field ? (sortDir === 'asc' ? '↑' : '↓') : '↕'}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {filtered.map((r, i) => (
                    <tr key={`${r.resourceId}-${i}`}>
                      <td title={r.subscriptionId}>{subNameMap.get(r.subscriptionId) || r.subscriptionId.slice(0, 8) + '...'}</td>
                      <td title={r.resourceGroup}>{r.resourceGroup.length > 20 ? r.resourceGroup.slice(0, 20) + '...' : r.resourceGroup}</td>
                      <td title={r.resourceName}><strong>{r.resourceName}</strong></td>
                      <td><span className="badge badge--info">{r.type}</span></td>
                      <td><ActionBadge action={classifyAction(r)} /></td>
                      <td><StatusBadge severity={r.priority} /></td>
                      <td className="text-success">{formatCurrency(r.estimatedMonthlySavings)}</td>
                      <td title={r.description}>{r.description.length > 60 ? r.description.slice(0, 60) + '...' : r.description}</td>
                    </tr>
                  ))}
                  {filtered.length === 0 && (
                    <tr>
                      <td colSpan={8} className="text-center">Nenhuma recomendação encontrada para os filtros selecionados.</td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>

          <div className="section">
            <p>
              <strong>{filtered.length}</strong> recomendações exibidas | Economia total: <strong>{formatCurrency(totalFilteredSavings)}</strong>/mês
            </p>
          </div>
        </>
      )}
    </div>
  );
}

function ActionBadge({ action }: { action: string }) {
  const cls = action === 'Excluir' ? 'badge--danger' : action === 'Investigar' ? 'badge--warning' : 'badge--info';
  return <span className={`badge ${cls}`}>{action}</span>;
}
