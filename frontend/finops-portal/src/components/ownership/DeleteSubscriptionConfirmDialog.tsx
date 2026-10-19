import type { SubscriptionOwnership, TeamOwnership } from '../../types/api';

interface DeleteSubscriptionConfirmDialogProps {
  team: TeamOwnership | null;
  subscription: SubscriptionOwnership | null;
  loading: boolean;
  onClose: () => void;
  onConfirm: () => Promise<void>;
}

export default function DeleteSubscriptionConfirmDialog({
  team,
  subscription,
  loading,
  onClose,
  onConfirm,
}: DeleteSubscriptionConfirmDialogProps) {
  if (!team || !subscription) return null;

  return (
    <div className="modal-overlay" role="dialog" aria-modal="true">
      <div className="modal">
        <h3>Remover assinatura</h3>
        <p>
          Deseja remover <strong>{subscription.subscriptionName}</strong> do time <strong>{team.name}</strong>?
        </p>
        <p className="text-muted font-mono">{subscription.subscriptionId}</p>

        <div className="modal-actions">
          <button type="button" className="btn btn--secondary" onClick={onClose} disabled={loading}>
            Cancelar
          </button>
          <button type="button" className="btn btn--ghost ownership-btn--danger" onClick={() => void onConfirm()} disabled={loading}>
            {loading ? 'Removendo...' : 'Remover'}
          </button>
        </div>
      </div>
    </div>
  );
}

