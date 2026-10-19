import { useState } from 'react';
import type { SubscriptionOwnership, TeamOwnership } from '../../types/api';

interface AddSubscriptionModalProps {
  team: TeamOwnership | null;
  loading: boolean;
  onClose: () => void;
  onSubmit: (payload: SubscriptionOwnership) => Promise<void>;
}

const initialState: SubscriptionOwnership = {
  subscriptionId: '',
  subscriptionName: '',
  environment: '',
  enabled: true,
};

export default function AddSubscriptionModal({ team, loading, onClose, onSubmit }: AddSubscriptionModalProps) {
  const [form, setForm] = useState<SubscriptionOwnership>(initialState);

  if (!team) return null;

  const canSubmit = form.subscriptionId.trim() !== '' && form.subscriptionName.trim() !== '';

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!canSubmit || loading) return;
    await onSubmit({
      subscriptionId: form.subscriptionId.trim(),
      subscriptionName: form.subscriptionName.trim(),
      environment: form.environment.trim(),
      enabled: form.enabled,
    });
    setForm(initialState);
  };

  return (
    <div className="modal-overlay" role="dialog" aria-modal="true">
      <div className="modal">
        <h3>Adicionar assinatura em {team.name}</h3>
        <form onSubmit={handleSubmit} className="ownership-form">
          <label>
            Subscription ID *
            <input
              value={form.subscriptionId}
              onChange={(e) => setForm((prev) => ({ ...prev, subscriptionId: e.target.value }))}
              placeholder="00000000-0000-0000-0000-000000000000"
              required
            />
          </label>
          <label>
            Nome da Subscription *
            <input
              value={form.subscriptionName}
              onChange={(e) => setForm((prev) => ({ ...prev, subscriptionName: e.target.value }))}
              placeholder="MPN - Plataforma - DEV"
              required
            />
          </label>
          <label>
            Ambiente
            <input
              value={form.environment}
              onChange={(e) => setForm((prev) => ({ ...prev, environment: e.target.value }))}
              placeholder="DEV"
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
              {loading ? 'Salvando...' : 'Salvar'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

