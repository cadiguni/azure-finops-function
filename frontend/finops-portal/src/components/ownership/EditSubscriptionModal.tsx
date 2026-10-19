import { useEffect, useState } from 'react';
import type { SubscriptionOwnership, TeamOwnership, UpdateSubscriptionOwnershipPayload } from '../../types/api';

interface EditSubscriptionModalProps {
  team: TeamOwnership | null;
  teams: TeamOwnership[];
  subscription: SubscriptionOwnership | null;
  loading: boolean;
  onClose: () => void;
  onSubmit: (payload: UpdateSubscriptionOwnershipPayload) => Promise<void>;
}

export default function EditSubscriptionModal({
  team,
  teams,
  subscription,
  loading,
  onClose,
  onSubmit,
}: EditSubscriptionModalProps) {
  const [form, setForm] = useState<UpdateSubscriptionOwnershipPayload>({
    subscriptionId: '',
    subscriptionName: '',
    environment: '',
    enabled: true,
    targetTeamId: '',
  });

  useEffect(() => {
    if (subscription && team) {
      setForm({
        ...subscription,
        targetTeamId: team.id,
      });
    }
  }, [subscription, team]);

  if (!team || !subscription) return null;

  const canSubmit = form.subscriptionId.trim() !== '' && form.subscriptionName.trim() !== '';

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!canSubmit || loading) return;
    await onSubmit({
      subscriptionId: form.subscriptionId.trim(),
      subscriptionName: form.subscriptionName.trim(),
      environment: form.environment.trim(),
      enabled: form.enabled,
      targetTeamId: form.targetTeamId?.trim() || team.id,
    });
  };

  return (
    <div className="modal-overlay" role="dialog" aria-modal="true">
      <div className="modal">
        <h3>Editar assinatura em {team.name}</h3>
        <form onSubmit={handleSubmit} className="ownership-form">
          <label>
            Time de destino
            <select
              value={form.targetTeamId ?? team.id}
              onChange={(e) => setForm((prev) => ({ ...prev, targetTeamId: e.target.value }))}
            >
              {teams.map((availableTeam) => (
                <option key={availableTeam.id} value={availableTeam.id}>
                  {availableTeam.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            Subscription ID *
            <input
              value={form.subscriptionId}
              onChange={(e) => setForm((prev) => ({ ...prev, subscriptionId: e.target.value }))}
              required
            />
          </label>
          <label>
            Nome da Subscription *
            <input
              value={form.subscriptionName}
              onChange={(e) => setForm((prev) => ({ ...prev, subscriptionName: e.target.value }))}
              required
            />
          </label>
          <label>
            Ambiente
            <input
              value={form.environment}
              onChange={(e) => setForm((prev) => ({ ...prev, environment: e.target.value }))}
            />
          </label>
          <label className="checkbox-label">
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(e) => setForm((prev) => ({ ...prev, enabled: e.target.checked }))}
            />
            Habilitada
          </label>

          <div className="modal-actions">
            <button type="button" className="btn btn--secondary" onClick={onClose} disabled={loading}>
              Cancelar
            </button>
            <button type="submit" className="btn btn--primary" disabled={!canSubmit || loading}>
              {loading ? 'Salvando...' : 'Salvar alterações'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
