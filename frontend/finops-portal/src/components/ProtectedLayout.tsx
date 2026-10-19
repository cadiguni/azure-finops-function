import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import { useEffect, useState } from 'react';
import type { CurrentUserInfo } from '../types/api';
import { api, ApiError } from '../services/api';
import Brand from './Brand';
import LoginButton from './LoginButton';
import { AccessProvider } from '../auth/AccessContext';

export default function ProtectedLayout({ children }: { children: React.ReactNode }) {
  const isAuthenticated = useIsAuthenticated();
  const { inProgress, instance } = useMsal();
  const [me, setMe] = useState<CurrentUserInfo | null>(null);
  const [loadingMe, setLoadingMe] = useState(false);
  const [errorMe, setErrorMe] = useState<string | null>(null);

  useEffect(() => {
    if (!isAuthenticated || inProgress !== InteractionStatus.None) {
      setMe(null);
      setErrorMe(null);
      return;
    }

    let cancelled = false;
    const loadMe = async () => {
      setLoadingMe(true);
      setErrorMe(null);
      try {
        const result = await api.me();
        if (!cancelled) {
          setMe(result);
        }
      } catch (error) {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 401) {
          await instance.loginRedirect().catch(() => undefined);
        }
        setMe(null);
        setErrorMe(error instanceof Error ? error.message : 'Falha ao validar permissões.');
      } finally {
        if (!cancelled) {
          setLoadingMe(false);
        }
      }
    };

    void loadMe();
    return () => {
      cancelled = true;
    };
  }, [instance, inProgress, isAuthenticated]);

  if (inProgress !== InteractionStatus.None) {
    return (
      <div className="login-page">
        <div className="login-card">
          <div className="login-header">
            <Brand context="login" />
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
            <Brand context="login" />
          </div>
          <p className="login-message">Faça login para acessar o portal.</p>
          <LoginButton />
        </div>
      </div>
    );
  }

  if (!loadingMe && !me && errorMe) {
    return (
      <div className="login-page">
        <div className="login-card">
          <div className="login-header">
            <Brand context="login" />
          </div>
          <p className="login-message">{errorMe}</p>
          <LoginButton />
        </div>
      </div>
    );
  }

  if (loadingMe || !me) {
    return (
      <div className="login-page">
        <div className="login-card">
          <div className="login-header">
            <Brand context="login" />
          </div>
          <p className="login-message">Validando permissões...</p>
        </div>
      </div>
    );
  }

  if (!me.isReader && !me.isAdmin) {
    return (
      <div className="login-page">
        <div className="login-card">
          <div className="login-header">
            <Brand context="login" />
          </div>
          <p className="login-message">Acesso negado: seu usuário não possui role FinOps.Reader ou FinOps.Admin.</p>
        </div>
      </div>
    );
  }

  return <AccessProvider value={me}>{children}</AccessProvider>;
}
