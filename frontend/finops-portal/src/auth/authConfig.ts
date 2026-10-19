import type { Configuration, RedirectRequest } from '@azure/msal-browser';

export const msalConfig: Configuration = {
  auth: {
    clientId: import.meta.env.VITE_ENTRA_CLIENT_ID,
    authority: `https://login.microsoftonline.com/${import.meta.env.VITE_ENTRA_TENANT_ID}`,
    redirectUri: import.meta.env.VITE_ENTRA_REDIRECT_URI || window.location.origin,
    postLogoutRedirectUri: import.meta.env.VITE_ENTRA_REDIRECT_URI || window.location.origin,
  },
  cache: {
    cacheLocation: 'localStorage',
  },
};

export const loginRequest: RedirectRequest = {
  scopes: ['User.Read'],
};

export const apiTokenRequest = {
  scopes: [import.meta.env.VITE_FINOPS_API_SCOPE],
};

// Migrações chamam o ARM com o token do próprio usuário: só é possível mover o que ele já pode mover no Azure.
// Exige a permissão delegada "Azure Service Management / user_impersonation" no app registration do portal.
export const armTokenRequest = {
  scopes: ['https://management.azure.com/user_impersonation'],
};
