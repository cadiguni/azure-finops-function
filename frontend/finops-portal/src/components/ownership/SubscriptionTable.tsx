import type { SubscriptionOwnership } from '../../types/api';

interface SubscriptionTableProps {
  subscriptions: SubscriptionOwnership[];
  isAdmin: boolean;
  columnWidths: Record<'subscriptionId' | 'name' | 'enabled' | 'actions', number>;
  onResizeColumn: (
    field: 'subscriptionId' | 'name' | 'enabled' | 'actions',
    event: React.MouseEvent<HTMLSpanElement>
  ) => void;
  onEdit: (subscription: SubscriptionOwnership) => void;
  onDelete: (subscription: SubscriptionOwnership) => void;
}

export default function SubscriptionTable({
  subscriptions,
  isAdmin,
  columnWidths,
  onResizeColumn,
  onEdit,
  onDelete,
}: SubscriptionTableProps) {
  if (subscriptions.length === 0) {
    return <div className="text-muted">Nenhuma subscription cadastrada.</div>;
  }

  return (
    <div className="table-container">
      <table className="ownership-table ownership-table--manual">
        <colgroup>
          <col style={{ width: `${columnWidths.subscriptionId}px` }} />
          <col style={{ width: `${columnWidths.name}px` }} />
          <col style={{ width: `${columnWidths.enabled}px` }} />
          {isAdmin && <col style={{ width: `${columnWidths.actions}px` }} />}
        </colgroup>
        <thead>
          <tr>
            <th>
              Subscription ID
              <span className="column-resize-handle" onMouseDown={(event) => onResizeColumn('subscriptionId', event)} />
            </th>
            <th>
              Nome
              <span className="column-resize-handle" onMouseDown={(event) => onResizeColumn('name', event)} />
            </th>
            <th>
              Habilitada
              <span className="column-resize-handle" onMouseDown={(event) => onResizeColumn('enabled', event)} />
            </th>
            {isAdmin && (
              <th>
                Ações
                <span className="column-resize-handle" onMouseDown={(event) => onResizeColumn('actions', event)} />
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {subscriptions.map((sub) => (
            <tr key={sub.subscriptionId}>
              <td className="font-mono">{sub.subscriptionId}</td>
              <td>{sub.subscriptionName}</td>
              <td>
                <span className={`badge ${sub.enabled ? 'badge--info' : 'badge--muted'}`}>
                  {sub.enabled ? 'Sim' : 'Não'}
                </span>
              </td>
              {isAdmin && (
                <td>
                  <div className="ownership-actions">
                    <button className="btn btn--ghost ownership-btn" onClick={() => onEdit(sub)}>Editar</button>
                    <button className="btn btn--ghost ownership-btn ownership-btn--danger" onClick={() => onDelete(sub)}>Remover</button>
                  </div>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
