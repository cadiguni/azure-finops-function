import { Fragment, useMemo, useState } from 'react';
import { useFetch } from '../hooks/useFetch';
import { governanceApi } from '../services/governanceApi';
import type { DataHealthOverviewRow, DataHealthResponse } from '../types/governance';

type Status = DataHealthOverviewRow['status'];

const statusLabels: Record<Status, string> = {
  unavailable: 'Indisponível', degraded: 'Publicação degradada', incomplete: 'Incompleta/defasada',
  unknown: 'Sem dados de export', covered: 'Coberta',
};
const statusBadge: Record<Status, string> = {
  unavailable: 'badge--danger', degraded: 'badge--danger', incomplete: 'badge--warning',
  unknown: 'badge--muted', covered: 'badge--success',
};
const statusOrder: Status[] = ['unavailable', 'degraded', 'incomplete', 'unknown', 'covered'];

function formatDateTime(value: string | null): string {
  return value ? new Date(value).toLocaleString('pt-BR') : '—';
}

export default function DataHealth() {
  const { data, loading, error, refetch } = useFetch(() => governanceApi.getDataHealthOverview(), []);
  const [statusFilter, setStatusFilter] = useState<Status | ''>('');
  const [search, setSearch] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);
  const [detail, setDetail] = useState<{ id: string; data?: DataHealthResponse; error?: string } | null>(null);

  const rows = useMemo(() => data?.subscriptions ?? [], [data?.subscriptions]);
  const counts = useMemo(() => {
    const result = new Map<Status, number>();
    rows.forEach((row) => result.set(row.status, (result.get(row.status) ?? 0) + 1));
    return result;
  }, [rows]);
  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    return rows.filter((row) => (!statusFilter || row.status === statusFilter)
      && (!term || row.subscriptionId.includes(term) || (row.subscriptionName ?? '').toLowerCase().includes(term)));
  }, [rows, search, statusFilter]);

  const toggleDetail = async (row: DataHealthOverviewRow) => {
    if (expanded === row.subscriptionId) { setExpanded(null); return; }
    setExpanded(row.subscriptionId);
    if (detail?.id === row.subscriptionId && detail.data) return;
    setDetail({ id: row.subscriptionId });
    try {
      const result = await governanceApi.getDataHealth(row.subscriptionId, data?.from, data?.to);
      setDetail({ id: row.subscriptionId, data: result });
    } catch (err) {
      setDetail({ id: row.subscriptionId, error: err instanceof Error ? err.message : 'Falha ao carregar detalhes.' });
    }
  };

  return <div className="page">
    <div className="page-header">
      <h2>Saúde dos dados</h2>
      {data && <span className="page-date">{data.from} a {data.to} · {rows.length} subscriptions</span>}
    </div>
    <p className="text-muted">
      Dias importados dos Cost Management Exports por subscription nos últimos 14 dias consolidados.
      Indica o que foi publicado no índice, não a execução dos timers.
    </p>

    <div className="filters filters--compact">
      <div className="filter-group">
        <label htmlFor="health-search">Subscription</label>
        <input id="health-search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Nome ou ID" />
      </div>
      <button className="btn btn--secondary" type="button" onClick={refetch} disabled={loading}>Atualizar</button>
    </div>

    {rows.length > 0 && <div className="recommendations-summary-badges" style={{ marginBottom: 16 }}>
      {statusOrder.filter((status) => counts.has(status)).map((status) => (
        <button key={status} type="button" className={`badge ${statusBadge[status]} badge--button`}
          aria-pressed={statusFilter === status}
          onClick={() => setStatusFilter((current) => current === status ? '' : status)}>
          {statusLabels[status]}: {counts.get(status)}
        </button>
      ))}
    </div>}

    {loading && <p className="loading" role="status">Avaliando subscriptions…</p>}
    {error && <div className="alert alert--warning" role="alert">{error}</div>}
    {!loading && !error && data && data.warnings.length > 0 && <div className="alert alert--warning">
      <ul>{data.warnings.map((warning) => <li key={warning}>{warning}</li>)}</ul>
    </div>}
    {!loading && !error && data && rows.length === 0 && <p>Nenhuma subscription conhecida para avaliar.</p>}

    {!loading && !error && filtered.length > 0 && <div className="table-container"><table>
      <thead><tr>
        <th>Subscription</th><th>Status</th><th>Cobertura</th><th>Dias faltando</th>
        <th>Último dia com dados</th><th>Última importação</th><th>Publicação</th><th></th>
      </tr></thead>
      <tbody>{filtered.map((row) => <Fragment key={row.subscriptionId}>
        <tr>
          <td>{row.subscriptionName ?? row.subscriptionId}
            <div className="text-muted" style={{ fontSize: '0.75rem' }}>{row.subscriptionId}</div></td>
          <td><span className={`badge ${statusBadge[row.status]}`}>{statusLabels[row.status] ?? row.status}</span>
            {!row.exportIngestion && <div className="text-muted" style={{ fontSize: '0.75rem' }}>fora da ingestão de exports</div>}</td>
          <td>{row.coveragePercent == null ? '—' : `${row.coveragePercent}%`}</td>
          <td>{row.missingDays ?? '—'}</td>
          <td className={row.isStale ? 'text-danger' : undefined}>
            {row.latestUsageDate ?? '—'}{row.isStale ? ' (defasado)' : ''}</td>
          <td>{formatDateTime(row.lastImportedAt)}</td>
          <td>{row.publicationMode}{row.lastPublicationStatus ? ` · ${row.lastPublicationStatus}` : ''}</td>
          <td><button type="button" className="btn btn--secondary" onClick={() => void toggleDetail(row)}>
            {expanded === row.subscriptionId ? 'Fechar' : 'Ver dias'}</button></td>
        </tr>
        {expanded === row.subscriptionId && <tr><td colSpan={8}>
          {detail?.id === row.subscriptionId && detail.error && <span className="text-danger">{detail.error}</span>}
          {detail?.id === row.subscriptionId && !detail.data && !detail.error && <span className="loading">Carregando…</span>}
          {detail?.id === row.subscriptionId && detail.data && (detail.data.missingDates == null
            ? 'Sem índice de export: não é possível listar os dias.'
            : detail.data.missingDates.length === 0
              ? 'Todos os dias da janela foram importados.'
              : `Dias sem dados: ${detail.data.missingDates.join(', ')}`)}
        </td></tr>}
      </Fragment>)}</tbody>
    </table></div>}
  </div>;
}
