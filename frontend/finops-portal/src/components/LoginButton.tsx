import { useMsal } from '@azure/msal-react';
import { loginRequest } from '../auth/authConfig';

export default function LoginButton() {
  const { instance } = useMsal();

  const handleLogin = () => {
    instance.loginRedirect(loginRequest).catch((error) => {
      console.error('Login failed:', error);
    });
  };

  return (
    <button className="btn btn-primary" onClick={handleLogin}>
      Entrar com Microsoft
    </button>
  );
}
