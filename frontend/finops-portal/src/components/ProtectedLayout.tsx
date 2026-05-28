import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import LoginButton from './LoginButton';

export default function ProtectedLayout({ children }: { children: React.ReactNode }) {
  const isAuthenticated = useIsAuthenticated();
  const { inProgress } = useMsal();

  if (inProgress !== InteractionStatus.None) {
    return (
      <div className="login-page">
        <div className="login-card">
          <div className="login-header">
            <h1>FinOps</h1>
            <span className="login-subtitle">Cost Platform</span>
          </div>
          <p className="login-message">Autenticando...</p>
        </div>
      </div>
    );
  }

  if (!isAuthenticated) {
    return (
      <div className="login-page">
        <div className="login-card">
          <div className="login-header">
            <h1>FinOps</h1>
            <span className="login-subtitle">Cost Platform</span>
          </div>
          <p className="login-message">Faça login para acessar o portal.</p>
          <LoginButton />
        </div>
      </div>
    );
  }

  return <>{children}</>;
}
