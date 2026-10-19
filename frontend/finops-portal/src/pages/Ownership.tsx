import { useEffect, useMemo, useState } from 'react';
import type { OwnershipConfig, OwnershipManagementGroupSyncResult, SubscriptionOwnership, TeamOwnership, UpdateSubscriptionOwnershipPayload } from '../types/api';
import { ownershipService } from '../services/ownershipService';
import TeamOwnershipCard from '../components/ownership/TeamOwnershipCard';
import AddSubscriptionModal from '../components/ownership/AddSubscriptionModal';
import EditSubscriptionModal from '../components/ownership/EditSubscriptionModal';
import DeleteSubscriptionConfirmDialog from '../components/ownership/DeleteSubscriptionConfirmDialog';
import { useAccess } from '../auth/AccessContext';
import { ApiError } from '../services/api';

type OwnershipColumnField = 'subscriptionId' | 'name' | 'enabled' | 'actions';

const DEFAULT_COLUMN_WIDTHS: Record<OwnershipColumnField, number> = {
  subscriptionId: 280,
  name: 240,
  enabled: 120,
  actions: 160,
};

const MINIMUM_COLUMN_WIDTHS: Record<OwnershipColumnField, number> = {
  subscriptionId: 180,
  name: 150,
  enabled: 100,
  actions: 120,
};

function normalizeSearch(value: string): string {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').trim().toLowerCase();
}

export default function Ownership() {
  const { isAdmin, isReader } = useAccess();

  const [data, setData] = useState<OwnershipConfig | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [info, setInfo] = useState('');
  const [syncResult, setSyncResult] = useState<OwnershipManagementGroupSyncResult | null>(null);
  const [actionLoading, setActionLoading] = useState(false);
  const [teamFilter, setTeamFilter] = useState('');
  const [subscriptionSearch, setSubscriptionSearch] = useState('');
  const [columnWidths, setColumnWidths] = useState<Record<OwnershipColumnField, number>>(() => {
    const stored = localStorage.getItem('ownership-column-widths');
    if (!stored) return DEFAULT_COLUMN_WIDTHS;
    try {
      const parsed = JSON.parse(stored) as Partial<Record<OwnershipColumnField, number>>;
      return { ...DEFAULT_COLUMN_WIDTHS, ...parsed };
    } catch {
      return DEFAULT_COLUMN_WIDTHS;
    }
  });
  const [resizingField, setResizingField] = useState<OwnershipColumnField | null>(null);
  const [resizeStartX, setResizeStartX] = useState(0);
  const [resizeStartWidth, setResizeStartWidth] = useState(0);

  const [teamForAdd, setTeamForAdd] = useState<TeamOwnership | null>(null);
  const [teamForEdit, setTeamForEdit] = useState<TeamOwnership | null>(null);
  const [subscriptionForEdit, setSubscriptionForEdit] = useState<SubscriptionOwnership | null>(null);
  const [teamForDelete, setTeamForDelete] = useState<TeamOwnership | null>(null);
  const [subscriptionForDelete, setSubscriptionForDelete] = useState<SubscriptionOwnership | null>(null);

  const loadOwnership = async () => {
    setLoading(true);
    setError('');
    try {
      const config = await ownershipService.getTeams();
      setData(config);
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 401) {
        setError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else if (status === 403) {
        setError('403: acesso negado.');
      } else if (status === 404) {
        setError('404: recurso/configuração não encontrada.');
      } else if (status === 500) {
        setError('500: erro inesperado.');
      } else {
        setError(`Erro ao carregar ownership: ${err instanceof Error ? err.message : String(err)}`);
      }
      setData(null);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadOwnership();
  }, []);

  useEffect(() => {
    localStorage.setItem('ownership-column-widths', JSON.stringify(columnWidths));
  }, [columnWidths]);

  useEffect(() => {
    if (!resizingField) return;

    const onMouseMove = (event: MouseEvent) => {
      const delta = event.clientX - resizeStartX;
      setColumnWidths((current) => ({
        ...current,
        [resizingField]: Math.max(MINIMUM_COLUMN_WIDTHS[resizingField], resizeStartWidth + delta),
      }));
    };

    const onMouseUp = () => {
      setResizingField(null);
    };

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
    return () => {
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };
  }, [resizeStartWidth, resizeStartX, resizingField]);

  const teams = useMemo(() => data?.teams ?? [], [data?.teams]);
  const subscriptions = useMemo(() => teams.flatMap((team) => team.subscriptions), [teams]);
  const filteredTeams = useMemo(() => {
    const query = normalizeSearch(subscriptionSearch);
    return teams
      .filter((team) => !teamFilter || team.id === teamFilter)
      .map((team) => ({
        ...team,
        subscriptions: query
          ? team.subscriptions.filter((subscription) =>
              normalizeSearch(subscription.subscriptionName).includes(query)
              || normalizeSearch(subscription.subscriptionId).includes(query))
          : team.subscriptions,
      }))
      .filter((team) => !query || team.subscriptions.length > 0);
  }, [subscriptionSearch, teamFilter, teams]);
  const filteredSubscriptionsCount = filteredTeams.reduce(
    (total, team) => total + team.subscriptions.length,
    0
  );
  const hasFilters = Boolean(teamFilter || subscriptionSearch.trim());
  const enabledSubscriptions = subscriptions.filter((subscription) => subscription.enabled).length;
  const disabledSubscriptions = subscriptions.length - enabledSubscriptions;
  const synchronizedSubscriptions = subscriptions.filter(
    (subscription) => subscription.source === 'managementGroup'
  ).length;
  const lastSyncLabel = data?.lastManagementGroupSyncAt
    ? new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' }).format(
        new Date(data.lastManagementGroupSyncAt)
      )
    : 'Nunca executada';

  const startResizeColumn = (field: OwnershipColumnField, event: React.MouseEvent<HTMLSpanElement>) => {
    event.preventDefault();
    event.stopPropagation();
    setResizingField(field);
    setResizeStartX(event.clientX);
    setResizeStartWidth(columnWidths[field]);
  };

  const handleAddSubscription = async (payload: SubscriptionOwnership) => {
    if (!teamForAdd) return;
    setActionLoading(true);
    setError('');
    setInfo('');
    try {
      await ownershipService.addSubscription(teamForAdd.id, payload);
      setTeamForAdd(null);
      await loadOwnership();
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 409) {
        setError('409: subscription já cadastrada para este time ou em outro time.');
      } else if (status === 403) {
        setError('403: acesso negado.');
      } else if (status === 401) {
        setError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else if (status === 404) {
        setError('404: recurso/configuração não encontrada.');
      } else if (status === 500) {
        setError('500: erro inesperado.');
      } else {
        setError(`Erro ao adicionar subscription: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setActionLoading(false);
    }
  };

  const handleUpdateSubscription = async (payload: UpdateSubscriptionOwnershipPayload) => {
    if (!teamForEdit || !subscriptionForEdit) return;
    setActionLoading(true);
    setError('');
    setInfo('');
    try {
      await ownershipService.updateSubscription(teamForEdit.id, subscriptionForEdit.subscriptionId, payload);
      setTeamForEdit(null);
      setSubscriptionForEdit(null);
      await loadOwnership();
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 409) {
        setError('409: subscription já cadastrada para este time ou em outro time.');
      } else if (status === 403) {
        setError('403: acesso negado.');
      } else if (status === 401) {
        setError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else if (status === 404) {
        setError('404: recurso/configuração não encontrada.');
      } else if (status === 500) {
        setError('500: erro inesperado.');
      } else {
        setError(`Erro ao atualizar subscription: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setActionLoading(false);
    }
  };

  const handleDeleteSubscription = async () => {
    if (!teamForDelete || !subscriptionForDelete) return;
    setActionLoading(true);
    setError('');
    setInfo('');
    try {
      await ownershipService.deleteSubscription(teamForDelete.id, subscriptionForDelete.subscriptionId);
      setTeamForDelete(null);
      setSubscriptionForDelete(null);
      await loadOwnership();
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 403) {
        setError('403: acesso negado.');
      } else if (status === 401) {
        setError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else if (status === 404) {
        setError('404: recurso/configuração não encontrada.');
      } else if (status === 500) {
        setError('500: erro inesperado.');
      } else {
        setError(`Erro ao remover subscription: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setActionLoading(false);
    }
  };

  const handleSyncFromManagementGroups = async () => {
    setActionLoading(true);
    setError('');
    setInfo('');
    setSyncResult(null);
    try {
      const result = await ownershipService.syncFromManagementGroups();
      setSyncResult(result);
      setInfo(
        `${result.message} Descobertas: ${result.totalSubscriptionsDiscovered}. Com parent: ${result.totalSubscriptionsWithParent ?? 0}. Times criados: ${result.createdTeams}. Adicionadas: ${result.added}. Movidas: ${result.movedAutomaticAssignments}. Times duplicados removidos: ${result.removedAutomaticTeams}. Já mapeadas: ${result.skippedAlreadyAssigned}. Conflitos: ${result.conflicts}. Sem time correspondente: ${result.skippedWithoutTeamMatch}. Sem parent: ${result.skippedWithoutParent}.`
      );
      await loadOwnership();
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 403) {
        setError('403: acesso negado.');
      } else if (status === 401) {
        setError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else if (status === 404) {
        setError('404: recurso/configuração não encontrada.');
      } else if (status === 500) {
        setError('500: erro inesperado.');
      } else {
        setError(`Erro ao sincronizar ownership: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setActionLoading(false);
    }
  };

  const handleKeepCurrentAssignments = async (subscriptionIds: string[]) => {
    setActionLoading(true);
    setError('');
    try {
      const result = await ownershipService.acceptManagementGroupOverrides(subscriptionIds);
      setSyncResult((current) => current ? {
        ...current,
        conflicts: Math.max(0, current.conflicts - result.accepted),
        conflictDetails: current.conflictDetails.filter(
          (conflict) => !subscriptionIds.includes(conflict.subscriptionId)
        ),
      } : null);
      setInfo(`${result.message} Mantidas no time atual: ${result.accepted}.`);
      await loadOwnership();
    } catch (err) {
      setError(`Erro ao registrar exceção: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setActionLoading(false);
    }
  };

  return (
    <div className="page">
      <div className="page-header">
        <h2>Ownership</h2>
        <span className="page-date">{teams.length} times cadastrados</span>
      </div>

      {!loading && data && (
        <div className="ownership-summary-grid">
          <div className="ownership-summary-card">
            <span>Subscriptions atribuídas</span>
            <strong>{subscriptions.length}</strong>
          </div>
          <div className="ownership-summary-card">
            <span>Ativas / desabilitadas</span>
            <strong>{enabledSubscriptions} / {disabledSubscriptions}</strong>
          </div>
          <div className="ownership-summary-card">
            <span>Origem Management Group</span>
            <strong>{synchronizedSubscriptions}</strong>
          </div>
          <div className="ownership-summary-card ownership-summary-card--wide">
            <span>Última sincronização</span>
            <strong>{lastSyncLabel}</strong>
            <small>
              {data.lastManagementGroupSyncCreatedTeams ?? 0} times criados · {data.lastManagementGroupSyncAdded ?? 0} adicionadas · {data.lastManagementGroupSyncConflicts ?? 0} conflitos
            </small>
          </div>
        </div>
      )}

      <div className="section">
        <div className="filters">
          <div className="filter-group">
            <label htmlFor="ownership-team-filter">Time</label>
            <select
              id="ownership-team-filter"
              value={teamFilter}
              onChange={(event) => setTeamFilter(event.target.value)}
            >
              <option value="">Todos</option>
              {teams.map((team) => (
                <option key={team.id} value={team.id}>{team.name}</option>
              ))}
            </select>
          </div>
          <div className="filter-group">
            <label htmlFor="ownership-subscription-search">Subscription</label>
            <input
              id="ownership-subscription-search"
              type="search"
              value={subscriptionSearch}
              onChange={(event) => setSubscriptionSearch(event.target.value)}
              placeholder="Nome ou subscriptionId"
            />
          </div>
          <div className="filter-group recommendations-column-actions">
            <label>&nbsp;</label>
            <button
              className="btn btn--secondary"
              disabled={!hasFilters}
              onClick={() => {
                setTeamFilter('');
                setSubscriptionSearch('');
              }}
            >
              Limpar filtros
            </button>
          </div>
          <div className="filter-group recommendations-column-actions">
            <label>&nbsp;</label>
            <button className="btn btn--secondary" onClick={() => setColumnWidths(DEFAULT_COLUMN_WIDTHS)}>
              Resetar larguras
            </button>
          </div>
          {isAdmin && (
            <div className="filter-group recommendations-column-actions">
              <label>&nbsp;</label>
              <button className="btn btn--secondary" onClick={handleSyncFromManagementGroups} disabled={actionLoading}>
                {actionLoading ? 'Sincronizando...' : 'Sincronizar por Parent Management Group'}
              </button>
            </div>
          )}
        </div>
        {!loading && !error && (
          <div className="text-muted">
            Exibindo {filteredTeams.length} {filteredTeams.length === 1 ? 'time' : 'times'} e{' '}
            {filteredSubscriptionsCount} {filteredSubscriptionsCount === 1 ? 'subscription' : 'subscriptions'}.
          </div>
        )}
      </div>

      {isReader && !isAdmin && (
        <div className="alert alert--warning">
          Você possui acesso somente leitura ao Ownership.
        </div>
      )}

      {loading && <div className="loading">Carregando times...</div>}
      {error && (
        <div className="alert alert--warning">
          {error}
        </div>
      )}
      {info && (
        <div className="alert">
          {info}
        </div>
      )}

      {syncResult && syncResult.conflictDetails.length > 0 && (
        <div className="section">
          <div className="section-header">
            <h3>Conflitos para revisão</h3>
            <div className="ownership-conflict-actions">
              <span className="badge badge--warning">{syncResult.conflictDetails.length}</span>
              <button
                className="btn btn--secondary"
                disabled={actionLoading}
                onClick={() => void handleKeepCurrentAssignments(
                  syncResult.conflictDetails.map((conflict) => conflict.subscriptionId)
                )}
              >
                Manter todos atuais
              </button>
            </div>
          </div>
          <p className="text-muted">
            A sincronização preservou a atribuição atual porque ela não corresponde ao Parent Management Group.
            Use Revisar para mover a subscription quando o destino sugerido estiver correto.
          </p>
          <div className="table-container">
            <table>
              <thead>
                <tr>
                  <th>Subscription</th>
                  <th>Time atual</th>
                  <th>Parent Management Group</th>
                  <th>Destino sugerido</th>
                  <th>Ação</th>
                </tr>
              </thead>
              <tbody>
                {syncResult.conflictDetails.map((conflict) => (
                  <tr key={conflict.subscriptionId}>
                    <td>
                      <strong>{conflict.subscriptionName || conflict.subscriptionId}</strong>
                      <div className="font-mono">{conflict.subscriptionId}</div>
                    </td>
                    <td>{conflict.currentTeamName || conflict.currentTeamId}</td>
                    <td>{conflict.managementGroupName || conflict.managementGroupId}</td>
                    <td>{conflict.suggestedTeamName || 'Sem time correspondente'}</td>
                    <td>
                      <div className="ownership-conflict-actions">
                        <button
                          className="btn btn--secondary"
                          onClick={() => {
                            const currentTeam = teams.find((team) => team.id === conflict.currentTeamId);
                            const subscription = currentTeam?.subscriptions.find(
                              (item) => item.subscriptionId === conflict.subscriptionId
                            );
                            if (currentTeam && subscription) {
                              setTeamForEdit(currentTeam);
                              setSubscriptionForEdit(subscription);
                            }
                          }}
                        >
                          Revisar
                        </button>
                        <button
                          className="btn btn--secondary"
                          disabled={actionLoading}
                          onClick={() => void handleKeepCurrentAssignments([conflict.subscriptionId])}
                        >
                          Manter atual
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {!loading && !error && (
        <div className="section">
          {teams.length === 0 ? (
            <div className="alert alert--warning">
              Nenhum time cadastrado no ownership.
            </div>
          ) : filteredTeams.length === 0 ? (
            <div className="alert alert--warning">
              Nenhum time ou subscription corresponde aos filtros informados.
            </div>
          ) : (
            <div className="ownership-list">
              {filteredTeams.map((team) => (
                <TeamOwnershipCard
                  key={team.id}
                  team={team}
                  isAdmin={isAdmin}
                  columnWidths={columnWidths}
                  onResizeColumn={startResizeColumn}
                  onAdd={(selectedTeam) => setTeamForAdd(selectedTeam)}
                  onEdit={(selectedTeam, selectedSubscription) => {
                    setTeamForEdit(selectedTeam);
                    setSubscriptionForEdit(selectedSubscription);
                  }}
                  onDelete={(selectedTeam, selectedSubscription) => {
                    setTeamForDelete(selectedTeam);
                    setSubscriptionForDelete(selectedSubscription);
                  }}
                />
              ))}
            </div>
          )}
        </div>
      )}

      <AddSubscriptionModal
        team={teamForAdd}
        loading={actionLoading}
        onClose={() => setTeamForAdd(null)}
        onSubmit={handleAddSubscription}
      />

      <EditSubscriptionModal
        team={teamForEdit}
        teams={teams}
        subscription={subscriptionForEdit}
        loading={actionLoading}
        onClose={() => {
          setTeamForEdit(null);
          setSubscriptionForEdit(null);
        }}
        onSubmit={handleUpdateSubscription}
      />

      <DeleteSubscriptionConfirmDialog
        team={teamForDelete}
        subscription={subscriptionForDelete}
        loading={actionLoading}
        onClose={() => {
          setTeamForDelete(null);
          setSubscriptionForDelete(null);
        }}
        onConfirm={handleDeleteSubscription}
      />
    </div>
  );
}
