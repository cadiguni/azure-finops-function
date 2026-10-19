interface BrandProps {
  context?: 'sidebar' | 'login';
}

export default function Brand({ context = 'sidebar' }: BrandProps) {
  return (
    <div className={`brand brand--${context}`} aria-label="FinOps">
      <img className="brand-mark" src="/favicon.svg" alt="" />
      <span className="brand-wordmark">
        <span>Fin</span><span className="brand-wordmark-accent">Ops</span>
      </span>
    </div>
  );
}