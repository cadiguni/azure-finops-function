import { useState } from 'react';
import { ExternalLink, Download, Copy, Check } from 'lucide-react';
import { api } from '../services/api';
import { useFetch } from '../hooks/useFetch';

export default function Reports() {
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [selectedSub, setSelectedSub] = useState('');
  const [selectedTeam, setSelectedTeam] = useState('');
  const [copied, setCopied] = useState(false);

  const { data: recs } = useFetch(
    () => api.getRecommendations(date).catch(() => null),
    [date]
  );

  const { data: teamsData } = useFetch(
    () => api.getTeams().catch((): { teams: never[] } => ({ teams: [] })),
    []
  );

  const subscriptions = recs?.bySubscription?.map(s => s.subscriptionId) ?? [];
  const teams = teamsData?.teams ?? [];

  // Build subscription id→name map from teams data
  const subNameMap = new Map<string, string>();
  teams.forEach(t => {
    t.subscriptionIds.forEach((id, i) => {
      if (t.subscriptionNames?.[i]) subNameMap.set(id, t.subscriptionNames[i]);
    });
  });

  const htmlUrl = api.getReportHtmlUrl(date, selectedSub || undefined, selectedTeam || undefined);
  const csvUrl = api.getReportCsvUrl(date, selectedSub || undefined, selectedTeam || undefined);

  const handleCopy = async () => {
    await navigator.clipboard.writeText(htmlUrl);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="page">
      <div className="page-header">
        <h2>Relatórios</h2>
      </div>

      <div className="filters">
        <div className="filter-group">
          <label htmlFor="report-date">Data</label>
          <input
            id="report-date"
            type="date"
            value={date}
            onChange={(e) => setDate(e.target.value)}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="report-sub">Subscription</label>
          <select
            id="report-sub"
            value={selectedSub}
            onChange={(e) => { setSelectedSub(e.target.value); setSelectedTeam(''); }}
          >
            <option value="">Todas</option>
            {subscriptions.map((s) => (
              <option key={s} value={s}>{subNameMap.get(s) || s}</option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="report-team">Time</label>
          <select
            id="report-team"
            value={selectedTeam}
            onChange={(e) => { setSelectedTeam(e.target.value); setSelectedSub(''); }}
          >
            <option value="">Todos</option>
            {teams.map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </div>
      </div>

      <div className="report-actions">
        <a
          href={htmlUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="btn btn--primary"
        >
          <ExternalLink size={16} />
          Abrir Relatório HTML
        </a>
        <a
          href={csvUrl}
          download
          className="btn btn--secondary"
        >
          <Download size={16} />
          Baixar CSV
        </a>
        <button onClick={handleCopy} className="btn btn--ghost">
          {copied ? <Check size={16} /> : <Copy size={16} />}
          {copied ? 'Copiado!' : 'Copiar Link'}
        </button>
      </div>

      <div className="section">
        <h3>Preview</h3>
        <div className="report-preview">
          <iframe
            src={htmlUrl}
            title="Relatório FinOps"
            style={{ width: '100%', height: '600px', border: '1px solid var(--border)' }}
          />
        </div>
      </div>
    </div>
  );
}
