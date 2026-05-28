interface StatusBadgeProps {
  severity: string;
}

const severityColors: Record<string, string> = {
  None: 'badge--muted',
  Low: 'badge--info',
  Medium: 'badge--warning',
  High: 'badge--danger',
  Critical: 'badge--critical',
};

export default function StatusBadge({ severity }: StatusBadgeProps) {
  const className = severityColors[severity] ?? 'badge--muted';
  return <span className={`badge ${className}`}>{severity}</span>;
}
