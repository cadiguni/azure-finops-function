import { createContext, useContext } from 'react';
import type { CurrentUserInfo } from '../types/api';

const AccessContext = createContext<CurrentUserInfo | null>(null);

export function AccessProvider({ value, children }: { value: CurrentUserInfo; children: React.ReactNode }) {
  return <AccessContext.Provider value={value}>{children}</AccessContext.Provider>;
}

export function useAccess(): CurrentUserInfo {
  const context = useContext(AccessContext);
  if (!context) {
    throw new Error('useAccess deve ser usado dentro de AccessProvider');
  }

  return context;
}
