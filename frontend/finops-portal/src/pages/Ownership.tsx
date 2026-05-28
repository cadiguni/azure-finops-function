import { api } from '../services/api';
import { useFetch } from '../hooks/useFetch';
import type { TeamsResponse } from '../types/api';

export default function Ownership() {
  const { data: teamsData, loading, error } = useFetch(
    () => api.getTeams().catch((): TeamsResponse => ({ teams: [], teamsCount: 0, lastUpdated: '' })),
    []
  );

  const teams = teamsData?.teams ?? [];

  return (
    <div className="page">
      <div className="page-header">
        <h2>Ownership</h2>
        <span className="page-date">{teams.length} times cadastrados</span>
      </div>

      {loading && <div className="loading">Carregando times...</div>}
      {error && (
        <div className="alert alert--warning">
          Não foi possível carregar times.<br /><small>{error}</small>
        </div>
      )}

      {!loading && !error && (
        <div className="section">
          {teams.length === 0 ? (
            <div className="alert alert--warning">
              Nenhum time cadastrado. Use POST /api/teams para criar times.
            </div>
          ) : (
            <div className="table-container">
              <table>
                <thead>
                  <tr>
                    <th>Time</th>
                    <th>ID</th>
                    <th>Contato</th>
                    <th>Subscriptions</th>
                  </tr>
                </thead>
                <tbody>
                  {teams.map((team) => (
                    <tr key={team.id}>
                      <td><strong>{team.name}</strong></td>
                      <td className="font-mono">{team.id}</td>
                      <td>{team.email || '—'}</td>
                      <td>
                        <div className="sub-list">
                          {team.subscriptionIds.length === 0 && <span className="text-muted">Nenhuma</span>}
                          {team.subscriptionIds.map((sub, i) => (
                            <span key={sub} className="badge badge--muted font-mono" title={sub}>
                              {team.subscriptionNames?.[i] || sub.slice(0, 8) + '...'}
                            </span>
                          ))}
                        </div>
                      </td>
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
