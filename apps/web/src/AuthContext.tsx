import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";

export type AuthUser = { id: { value: string }; username: string; displayName: string; status: "Enabled" | "Disabled" | 0 | 1 };
export const isUserEnabled = (user: AuthUser) => user.status === "Enabled" || user.status === 0;
export type AuthSession = { id: { value: string }; userId: { value: string }; expiresAt: string };
export type CurrentUser = { user: AuthUser; session: AuthSession; effectivePermissions: string[] };
export const sessionStorageKey = "hispania.identity.session";

export function sessionFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  const token = window.sessionStorage.getItem(sessionStorageKey);
  if (token) headers.set("Authorization", `Session ${token}`);
  if (init.body && !headers.has("content-type")) headers.set("content-type", "application/json");
  return fetch(path, { ...init, headers });
}

type AuthContextValue = {
  currentUser: CurrentUser | null;
  token: string | null;
  loading: boolean;
  error: string | null;
  hasPermission: (permission: string) => boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  request: (path: string, init?: RequestInit) => Promise<Response>;
};
const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(() => window.sessionStorage.getItem(sessionStorageKey));
  const [currentUser, setCurrentUser] = useState<CurrentUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const request = useCallback((path: string, init: RequestInit = {}) => {
    const headers = new Headers(init.headers);
    if (token) headers.set("Authorization", `Session ${token}`);
    if (init.body && !headers.has("content-type")) headers.set("content-type", "application/json");
    return fetch(path, { ...init, headers }).then(response => {
      if (response.status === 401 && window.sessionStorage.getItem(sessionStorageKey) === token) {
        window.sessionStorage.removeItem(sessionStorageKey); setToken(null); setCurrentUser(null);
      }
      return response;
    });
  }, [token]);

  const refresh = useCallback(async (sessionToken: string) => {
    const response = await fetch("/api/identity/me", { headers: { Authorization: `Session ${sessionToken}` } });
    if (!response.ok) throw new Error("La sesión ya no es válida.");
    setCurrentUser(await response.json() as CurrentUser);
  }, []);

  useEffect(() => {
    if (!token) { setCurrentUser(null); setLoading(false); return; }
    let active = true;
    void refresh(token).catch(() => {
      if (!active) return;
      window.sessionStorage.removeItem(sessionStorageKey);
      setToken(null);
      setCurrentUser(null);
    }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [refresh, token]);

  const login = useCallback(async (username: string, password: string) => {
    setError(null);
    const response = await fetch("/api/identity/login", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ username, password }) });
    if (!response.ok) { setError("Usuario o contraseña incorrectos."); throw new Error("No se pudo iniciar sesión."); }
    const result = await response.json() as { sessionToken: string };
    window.sessionStorage.setItem(sessionStorageKey, result.sessionToken);
    setToken(result.sessionToken);
    try { await refresh(result.sessionToken); }
    catch (cause) { window.sessionStorage.removeItem(sessionStorageKey); setToken(null); throw cause; }
  }, [refresh]);

  const logout = useCallback(async () => {
    const activeToken = token;
    try {
      if (activeToken) await fetch("/api/identity/logout", { method: "POST", headers: { Authorization: `Session ${activeToken}` } });
    } finally {
      window.sessionStorage.removeItem(sessionStorageKey);
      setToken(null);
      setCurrentUser(null);
      setError(null);
    }
  }, [token]);

  const value = useMemo<AuthContextValue>(() => ({ currentUser, token, loading, error, login, logout, request, hasPermission: permission => currentUser?.effectivePermissions.includes(permission) === true || (permission.startsWith("platform.") && currentUser?.effectivePermissions.includes("platform.admin") === true) }), [currentUser, token, loading, error, login, logout, request]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used inside AuthProvider.");
  return value;
}
