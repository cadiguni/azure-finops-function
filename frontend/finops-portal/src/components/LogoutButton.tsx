import { useMsal } from '@azure/msal-react';

export default function LogoutButton() {
  const { instance } = useMsal();

  const handleLogout = () => {
    instance.logoutRedirect().catch((error) => {
      console.error('Logout failed:', error);
    });
  };

  return (
    <button className="btn btn-secondary" onClick={handleLogout}>
      Sair
    </button>
  );
}
