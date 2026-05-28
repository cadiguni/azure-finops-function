interface CardProps {
  title: string;
  value: string | number;
  subtitle?: string;
  icon?: React.ReactNode;
  variant?: 'default' | 'success' | 'warning' | 'danger';
}

export default function Card({ title, value, subtitle, icon, variant = 'default' }: CardProps) {
  return (
    <div className={`card card--${variant}`}>
      <div className="card-header">
        {icon && <span className="card-icon">{icon}</span>}
        <span className="card-title">{title}</span>
      </div>
      <div className="card-value">{value}</div>
      {subtitle && <div className="card-subtitle">{subtitle}</div>}
    </div>
  );
}
