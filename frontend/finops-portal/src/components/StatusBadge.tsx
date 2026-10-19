interface StatusBadgeProps {
  severity: string;
  label?: string;
}

const severityColors: Record<string, string> = {
  None: 'badge--muted',
  Low: 'badge--info',
  Medium: 'badge--warning',
  High: 'badge--danger',
  Critical: 'badge--critical',
  Consolidated: 'badge--info',
  InsufficientData: 'badge--warning',
  NoCostData: 'badge--muted',
  QueryFailed: 'badge--danger',
};

export default function StatusBadge({ severity, label }: StatusBadgeProps) {
  const className = severityColors[severity] ?? 'badge--muted';
  return <span className={`badge ${className}`}>{label ?? severity}</span>;
}
