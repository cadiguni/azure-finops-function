import type { SubscriptionOwnership, TeamOwnership } from '../../types/api';
import SubscriptionTable from './SubscriptionTable';

interface TeamOwnershipCardProps {
  team: TeamOwnership;
  isAdmin: boolean;
  columnWidths: Record<'subscriptionId' | 'name' | 'enabled' | 'actions', number>;
  onResizeColumn: (
    field: 'subscriptionId' | 'name' | 'enabled' | 'actions',
    event: React.MouseEvent<HTMLSpanElement>
  ) => void;
  onAdd: (team: TeamOwnership) => void;
  onEdit: (team: TeamOwnership, subscription: SubscriptionOwnership) => void;
  onDelete: (team: TeamOwnership, subscription: SubscriptionOwnership) => void;
}

export default function TeamOwnershipCard({
  team,
  isAdmin,
  columnWidths,
  onResizeColumn,
  onAdd,
  onEdit,
  onDelete,
}: TeamOwnershipCardProps) {
  return (
    <div className="card ownership-card">
      <div className="ownership-card-header">
        <div>
          <h3>{team.name}</h3>
          <div className="ownership-meta">
            <span className="font-mono">{team.id}</span>
            {team.requiredRole && <span className="badge badge--muted">{team.requiredRole}</span>}
            <span className="badge badge--info">{team.subscriptions.length} subscriptions</span>
          </div>
        </div>
        {isAdmin && (
          <button className="btn btn--primary" onClick={() => onAdd(team)}>
            Adicionar assinatura
          </button>
        )}
      </div>

      <SubscriptionTable
        subscriptions={team.subscriptions}
        isAdmin={isAdmin}
        columnWidths={columnWidths}
        onResizeColumn={onResizeColumn}
        onEdit={(subscription) => onEdit(team, subscription)}
        onDelete={(subscription) => onDelete(team, subscription)}
      />
    </div>
  );
}
