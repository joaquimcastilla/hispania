import { DynamicNavigation } from "./DynamicWeb";
import { useAuth } from "./AuthContext";
import { LoginPage } from "./LoginPage";
import { adminRoutes, adminEntryPath } from "./webModules";
export { adminEntryPath } from "./webModules";
import "./AuthorizationPages.css";

export function AdminShell({ path, variant }: { path: string; variant: "modern" | "classic" }) {
  const auth = useAuth();
  if (auth.loading) return <main className="authorization-shell" role="status">Comprobando sesión…</main>;
  if (!auth.currentUser) {
    window.history.replaceState({}, "", `/login?returnTo=${encodeURIComponent(path)}`);
    return <LoginPage />;
  }
  let route = adminRoutes.find(item => item.path === path);
  // A partial administrator enters the first section they can actually use.
  if (path === "/admin" && !auth.hasPermission("platform.observability.read")) {
    route = adminRoutes.find(item => auth.hasPermission(item.permission));
    if (route) window.history.replaceState({}, "", route.path);
  }
  if (!route || !auth.hasPermission(route.permission)) return <main className="authorization-shell"><h1>{route ? "Acceso denegado" : adminEntryPath(auth.hasPermission) ? "Página no encontrada" : "Acceso denegado"}</h1><p>No tienes acceso a esta página.</p><a href="/">Volver a HISPANIA</a></main>;
  const Page = route.component;
  return <main className={`authorization-admin admin-${variant}`}>
    <header className="authorization-header"><a href="/">HISPANIA</a><strong>Platform Administration</strong><span>{auth.currentUser.user.displayName}</span><button type="button" onClick={() => void auth.logout()}>Cerrar sesión</button></header>
    <div className="platform-admin-layout"><nav className="admin-navigation" aria-label="Administración">{adminRoutes.filter(item => auth.hasPermission(item.permission)).map(item => <a key={item.path} href={item.path} aria-current={item.path === route.path ? "page" : undefined}>{item.label}</a>)}<DynamicNavigation section="AdminNavigation" /></nav><div className="admin-page"><Page key={route.path} /></div></div>
  </main>;
}
