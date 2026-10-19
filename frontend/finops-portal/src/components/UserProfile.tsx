import { useMsal } from '@azure/msal-react';
import { LogOut } from 'lucide-react';

export default function UserProfile({ collapsed = false }: { collapsed?: boolean }) {
  const { instance } = useMsal();
  const account = instance.getActiveAccount();

  if (!account) return null;

  const displayName = account.name ?? account.username ?? 'Usuário';
  const email = account.username ?? '';

  const handleLogout = () => {
    instance.logoutRedirect().catch((error) => {
      console.error('Logout failed:', error);
    });
  };

  if (collapsed) {
    return (
      <div className="user-profile user-profile--collapsed">
        <button className="user-logout" onClick={handleLogout} title={displayName}>
          <LogOut size={16} />
        </button>
      </div>
    );
  }

  return (
    <div className="user-profile">
      <div className="user-info">
        <span className="user-name">{displayName}</span>
        {email && <span className="user-email">{email}</span>}
      </div>
      <button className="user-logout" onClick={handleLogout} title="Sair">
        <LogOut size={16} />
      </button>
    </div>
  );
}
