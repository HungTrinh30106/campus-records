import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, ApiError, resetCsrf } from './api';
import type { User } from './types';

interface AuthState { user: User | null; setUser: (user: User | null) => void; loading: boolean; error: string; reload: () => void }
const AuthContext = createContext<AuthState | null>(null);
export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const reload = useCallback(() => {
    setLoading(true); setError('');
    api<User>('/auth/me').then(setUser).catch(e => { setUser(null); if (!(e instanceof ApiError && e.status === 401)) setError(e.message); }).finally(() => setLoading(false));
  }, []);
  useEffect(() => { reload(); const expire = () => { setUser(null); resetCsrf(); }; window.addEventListener('session-expired', expire); return () => window.removeEventListener('session-expired', expire); }, [reload]);
  return <AuthContext.Provider value={{ user, setUser, loading, error, reload }}>{children}</AuthContext.Provider>;
}
export function useAuth() { const context = useContext(AuthContext); if (!context) throw new Error('AuthProvider is required'); return context; }
