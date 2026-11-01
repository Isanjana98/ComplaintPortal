import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { api, refreshSession, setAccessToken, setSessionExpiredHandler } from '../api/client';

const AuthContext = createContext(null);

export function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [loading, setLoading] = useState(true);

  const applySession = useCallback((data) => {
    setAccessToken(data?.accessToken ?? null);
    setUser(data?.user ?? null);
  }, []);

  // Restore the session from the HttpOnly refresh cookie on page load.
  useEffect(() => {
    setSessionExpiredHandler(() => applySession(null));
    refreshSession()
      .then((data) => applySession(data))
      .finally(() => setLoading(false));
  }, [applySession]);

  // Silently renew the short-lived access token before it expires.
  useEffect(() => {
    if (!user) return undefined;
    const id = setInterval(async () => {
      const data = await refreshSession();
      applySession(data);
    }, 12 * 60 * 1000);
    return () => clearInterval(id);
  }, [user, applySession]);

  const login = useCallback(async (email, password) => {
    const data = await api('/auth/login', { method: 'POST', body: { email, password } });
    applySession(data);
    return data.user;
  }, [applySession]);

  const register = useCallback(async (payload) => {
    const data = await api('/auth/register', { method: 'POST', body: payload });
    applySession(data);
    return data.user;
  }, [applySession]);

  const logout = useCallback(async () => {
    try { await api('/auth/logout', { method: 'POST' }); } catch { /* ignore */ }
    applySession(null);
  }, [applySession]);

  const changePassword = useCallback(async (currentPassword, newPassword) => {
    const data = await api('/auth/change-password', { method: 'POST', body: { currentPassword, newPassword } });
    applySession(data);
  }, [applySession]);

  // Permissions only drive what the UI shows - the API enforces every one of them again.
  const can = useCallback((...perms) => perms.some((p) => user?.permissions?.includes(p)), [user]);

  const value = useMemo(() => ({
    user,
    loading,
    can,
    isStaff: can('complaints.view_all'),
    isAdmin: user?.role === 'Admin',
    mustChangePassword: !!user?.mustChangePassword,
    login,
    register,
    logout,
    changePassword,
  }), [user, loading, can, login, register, logout, changePassword]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export const useAuth = () => useContext(AuthContext);
