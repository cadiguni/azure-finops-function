import { useState, useMemo } from 'react';
import StatusBadge from '../components/StatusBadge';
import { api } from '../services/api';
import { useFetch } from '../hooks/useFetch';
import type { CostAnomaly } from '../types/api';

function formatCurrency(value: number): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
}

function formatPercent(value: number): string {
  return `${value >= 0 ? '+' : ''}${value.toFixed(1)}%`;
}

type SortField = 'subscription' | 'todayCost' | 'averageLastDays' | 'increasePercent' | 'dailyBudget' | 'monthlyProjection' | 'severity';
type SortDir = 'asc' | 'desc';

const severityOrder: Record<string, number> = { Critical: 0, High: 1, Medium: 2, Low: 3, Info: 4 };

export default function Anomalies() {
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [days, setDays] = useState(3);
  const [filterSub, setFilterSub] = useState('');
  const [sortField, setSortField] = useState<SortField>('todayCost');
  const [sortDir, setSortDir] = useState<SortDir>('desc');

  const { data: anomalies, loading } = useFetch(
    () => api.getAnomalies(date, days).catch((): CostAnomaly[] => []),
    [date, days]
  );

  const items = anomalies ?? [];

  // Deduplicate: keep only the most recent entry per subscription
  const deduplicated = useMemo(() => {
    const map = new Map<string, CostAnomaly>();
    items.forEach(a => {
      const existing = map.get(a.subscriptionId);
      if (!existing || a.date > existing.date) {
        map.set(a.subscriptionId, a);
      }
    });
    return [...map.values()];
  }, [items]);

  // Build subscription id→name map
  const subNameMap = useMemo(() => {
    const m = new Map<string, string>();
    items.forEach(a => {
      if (a.subscriptionName && !m.has(a.subscriptionId)) {
        m.set(a.subscriptionId, a.subscriptionName);
      }
    });
    return m;
  }, [items]);

  const uniqueSubs = useMemo(() => [...new Set(items.map(a => a.subscriptionId))], [items]);

  const filtered = filterSub ? deduplicated.filter(a => a.subscriptionId === filterSub) : deduplicated;

  // Sort
  const sorted = useMemo(() => {
    const mul = sortDir === 'asc' ? 1 : -1;
    return [...filtered].sort((a, b) => {
      switch (sortField) {
        case 'subscription': return mul * (a.subscriptionName || a.subscriptionId).localeCompare(b.subscriptionName || b.subscriptionId);
        case 'todayCost': return mul * (a.todayCost - b.todayCost);
        case 'averageLastDays': return mul * (a.averageLastDays - b.averageLastDays);
        case 'increasePercent': return mul * (a.increasePercent - b.increasePercent);
        case 'dailyBudget': return mul * (a.dailyBudget - b.dailyBudget);
        case 'monthlyProjection': return mul * (a.monthlyProjection - b.monthlyProjection);
        case 'severity': return mul * ((severityOrder[a.severity] ?? 9) - (severityOrder[b.severity] ?? 9));
        default: return 0;
      }
    });
  }, [filtered, sortField, sortDir]);

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    } else {
      setSortField(field);
      setSortDir('desc');
    }
  };

  const sortIcon = (field: SortField) => {
    if (sortField !== field) return ' ↕';
    return sortDir === 'asc' ? ' ↑' : ' ↓';
  };

  return (
    <div className="page">
      <div className="page-header">
        <h2>Anomalias de Custo</h2>
        <span className="page-date">{sorted.filter(a => a.hasAnomaly).length} anomalias detectadas</span>
      </div>

      <div className="filters">
        <div className="filter-group">
          <label htmlFor="anom-date">Data</label>
          <input
            id="anom-date"
            type="date"
            value={date}
            onChange={(e) => setDate(e.target.value)}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="anom-days">Período (dias)</label>
          <select
            id="anom-days"
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
          >
            <option value={1}>1 dia</option>
            <option value={3}>3 dias</option>
            <option value={7}>7 dias</option>
            <option value={14}>14 dias</option>
            <option value={30}>30 dias</option>
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="anom-sub">Subscription</label>
          <select
            id="anom-sub"
            value={filterSub}
            onChange={(e) => setFilterSub(e.target.value)}
          >
            <option value="">Todas</option>
            {uniqueSubs.map((s) => (
              <option key={s} value={s}>{subNameMap.get(s) || s}</option>
            ))}
          </select>
        </div>
      </div>

      {loading && <div className="loading">Carregando anomalias...</div>}

      {!loading && (
        <div className="section">
          {sorted.length === 0 ? (
            <div className="alert alert--warning">
              Nenhuma anomalia encontrada para o período selecionado.
              <br />
              <small>Execute POST /api/cost-anomalies/run para gerar dados de anomalia.</small>
            </div>
          ) : (
            <div className="table-container">
              <table>
                <thead>
                  <tr>
                    <th className="sortable" onClick={() => handleSort('subscription')}>Subscription{sortIcon('subscription')}</th>
                    <th className="sortable" onClick={() => handleSort('todayCost')}>Custo Atual{sortIcon('todayCost')}</th>
                    <th className="sortable" onClick={() => handleSort('averageLastDays')}>Média {days > 1 ? `${days}d` : '1d'}{sortIcon('averageLastDays')}</th>
                    <th className="sortable" onClick={() => handleSort('increasePercent')}>Variação{sortIcon('increasePercent')}</th>
                    <th className="sortable" onClick={() => handleSort('dailyBudget')}>Meta Diária{sortIcon('dailyBudget')}</th>
                    <th className="sortable" onClick={() => handleSort('monthlyProjection')}>Projeção Mensal{sortIcon('monthlyProjection')}</th>
                    <th className="sortable" onClick={() => handleSort('severity')}>Severidade{sortIcon('severity')}</th>
                  </tr>
                </thead>
                <tbody>
                  {sorted.map((a) => (
                    <tr key={a.subscriptionId} className={a.hasAnomaly ? 'row--highlight' : ''}>
                      <td title={a.subscriptionId}>
                        {a.subscriptionName || a.subscriptionId.slice(0, 12) + '...'}
                      </td>
                      <td>{formatCurrency(a.todayCost)}</td>
                      <td>{formatCurrency(a.averageLastDays)}</td>
                      <td className={a.increasePercent > 0 ? 'text-danger' : 'text-success'}>
                        {formatPercent(a.increasePercent)}
                      </td>
                      <td>{formatCurrency(a.dailyBudget)}</td>
                      <td>{formatCurrency(a.monthlyProjection)}</td>
                      <td><StatusBadge severity={a.severity} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
