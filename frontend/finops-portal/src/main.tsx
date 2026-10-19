import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { HashRouter } from 'react-router-dom'
import { MsalProvider } from '@azure/msal-react'
import { initializeMsal, msalInstance } from './auth/msalInstance'
import './index.css'
import App from './App.tsx'

initializeMsal().then(() => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <MsalProvider instance={msalInstance}>
        <HashRouter>
          <App />
        </HashRouter>
      </MsalProvider>
    </StrictMode>,
  );
});
