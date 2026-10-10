import { Component, createContext, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { useAuth } from "./AuthContext";
import { routeForPath } from "./webModules";

export type WebComponent = { type: "heading" | "text" | "status" | "table" | "button"; text: string; operation?: "platform.health" | "platform.health.refresh" | null };
export type WebContribution = { packageId: string; packageVersion: string; contentHash: string; page: { id: string; route: string; title: string; permission: string; navigation: { icon: string; section: "PrimaryNavigation" | "AdminNavigation"; order: number; asset?: string | null }; components: WebComponent[] } };
type Registry = { loading: boolean; error: boolean; contributions: readonly WebContribution[] };
const RegistryContext = createContext<Registry>({ loading: true, error: false, contributions: [] });

export function validContribution(item: WebContribution): boolean {
  const id = /^[a-z0-9][a-z0-9._-]{0,127}$/;
  return !!item && id.test(item.packageId) && !!item.page && id.test(item.page.id) &&
    item.page.route === `/extensions/${item.packageId}/${item.page.id}` && typeof item.page.title === "string" && item.page.title.length <= 128 &&
    typeof item.page.permission === "string" && !!item.page.navigation && ["PrimaryNavigation", "AdminNavigation"].includes(item.page.navigation.section) &&
    ["package", "status", "table"].includes(item.page.navigation.icon) && Number.isInteger(item.page.navigation.order) && Math.abs(item.page.navigation.order) <= 1000 &&
    (!item.page.navigation.asset || /^web\/assets\/[a-zA-Z0-9_-]+\.png$/.test(item.page.navigation.asset)) && /^[A-Fa-f0-9]{64}$/.test(item.contentHash) &&
    Array.isArray(item.page.components) && item.page.components.length > 0 && item.page.components.length <= 32 && item.page.components.every(c =>
      !!c && typeof c.text === "string" && c.text.length <= 2048 && ["heading", "text", "status", "table", "button"].includes(c.type) &&
      (c.type === "button" ? c.operation === "platform.health.refresh" : c.type === "status" || c.type === "table" ? c.operation === "platform.health" : !c.operation));
}

export function WebContributionProvider({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const [state, setState] = useState<Registry>({ loading: true, error: false, contributions: [] });
  useEffect(() => {
    let active = true;
    if (!auth.currentUser) { setState({ loading: false, error: false, contributions: [] }); return; }
    setState({ loading: true, error: false, contributions: [] });
    void Promise.resolve().then(() => auth.request("/api/v1/web/contributions")).then(async response => {
      if (!response.ok) throw new Error("registry");
      const result = await response.json() as { schemaGeneration: number; contributions: WebContribution[] };
      if (result.schemaGeneration !== 1 || !Array.isArray(result.contributions) || result.contributions.length > 4096) throw new Error("schema");
      const contributions = result.contributions.filter(validContribution);
      if (new Set(contributions.map(item => item.page.route)).size !== contributions.length) throw new Error("collision");
      if (active) setState({ loading: false, error: false, contributions });
    }).catch(() => { if (active) setState({ loading: false, error: true, contributions: [] }); });
    return () => { active = false; };
  }, [auth.currentUser, auth.request]);
  return <RegistryContext.Provider value={state}>{children}</RegistryContext.Provider>;
}

export function DynamicNavigation({ section = "PrimaryNavigation", className = "nav-item" }: { className?: string; section?: "PrimaryNavigation" | "AdminNavigation" }) {
  const registry = useContext(RegistryContext);
  const auth = useAuth();
  return <>{registry.contributions.filter(item => item.page.navigation.section === section && auth.hasPermission(item.page.permission)).map(item =>
    <a className={className} key={`${item.packageId}/${item.page.id}`} href={item.page.route}>{item.page.navigation.asset ? <img width={24} height={24} alt="" src={`/api/v1/web/assets/${item.packageId}/${item.contentHash}/${item.page.navigation.asset.slice(11)}`} /> : <span aria-hidden="true">{({ package: "▣", status: "●", table: "▦" } as Record<string, string>)[item.page.navigation.icon]} </span>}{item.page.title}</a>)}</>;
}

class PageBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() { return this.state.failed ? <p role="alert">Contribución no disponible.</p> : this.props.children; }
}

export function DynamicPage({ path, variant }: { path: string; variant: "modern" | "classic" }) {
  const registry = useContext(RegistryContext);
  const auth = useAuth();
  const candidate = routeForPath(path, registry.contributions)?.contribution;
  const item = candidate && auth.hasPermission(candidate.page.permission) ? candidate : undefined;
  const title = useRef<HTMLHeadingElement>(null);
  useEffect(() => { title.current?.focus(); }, [path, item]);
  return <main className={`authorization-admin admin-${variant}`}><header className="authorization-header"><a href="/">HISPANIA</a><nav aria-label="Extensiones"><DynamicNavigation /><DynamicNavigation section="AdminNavigation" /></nav></header>
    {registry.loading ? <p role="status">Cargando contribuciones…</p> : !item ? <h1>{registry.error ? "Contribuciones no disponibles" : "Página no encontrada"}</h1> :
      <PageBoundary key={`${item.contentHash}/${item.page.id}`}><section aria-labelledby="dynamic-title"><h1 id="dynamic-title" tabIndex={-1} ref={title}>{item.page.title}</h1>{item.page.components.map((component, index) => <DynamicComponent key={index} item={item} component={component} />)}</section></PageBoundary>}
  </main>;
}

function DynamicComponent({ item, component }: { item: WebContribution; component: WebComponent }) {
  const auth = useAuth();
  const [rows, setRows] = useState<{ name: string; value: string }[]>([]);
  const [state, setState] = useState("Cargando…");
  const [busy, setBusy] = useState(false);
  const endpoint = `/api/v1/web/contributions/${encodeURIComponent(item.packageId)}/${encodeURIComponent(item.page.id)}/operations/${component.operation}`;
  const load = async (method: "GET" | "POST", signal?: AbortSignal) => {
    const response = await auth.request(endpoint, { method, signal });
    if (!response.ok) { setState(response.status === 401 ? "Sesión caducada" : response.status === 403 ? "Acceso denegado" : response.status === 404 ? "Contribución no disponible; recarga la página" : "Error de operación"); return; }
    const data = await response.json() as { rows?: { name: string; value: string }[] };
    if (method === "GET") {
      if (!Array.isArray(data.rows) || data.rows.length > 100 || !data.rows.every(row => typeof row.name === "string" && row.name.length <= 2048 && typeof row.value === "string" && row.value.length <= 2048)) throw new Error("data");
      setRows(data.rows);
    }
    setState(method === "POST" ? "Acción aceptada" : "Datos cargados");
  };
  useEffect(() => {
    if (component.type !== "status" && component.type !== "table") return;
    const controller = new AbortController();
    void load("GET", controller.signal).catch(() => { if (!controller.signal.aborted) setState("Datos no disponibles"); });
    return () => controller.abort();
  }, [endpoint, auth.request]);
  if (component.type === "heading") return <h2>{component.text}</h2>;
  if (component.type === "text") return <p>{component.text}</p>;
  if (component.type === "button") return <div><button type="button" disabled={busy || !auth.hasPermission("platform.observability.read")} onClick={() => { setBusy(true); void load("POST").catch(() => setState("Error de operación")).finally(() => setBusy(false)); }}>{component.text}</button><p role="status">{busy ? "Enviando…" : state === "Cargando…" ? "" : state}</p></div>;
  if (component.type === "table" || component.type === "status") return <section><h2>{component.text}</h2><p role="status">{state}</p>{rows.length ? <table><thead><tr><th scope="col">Nombre</th><th scope="col">Estado</th></tr></thead><tbody>{rows.map((row, index) => <tr key={index}><td>{row.name}</td><td>{row.value}</td></tr>)}</tbody></table> : state === "Datos cargados" && <p>Sin resultados.</p>}</section>;
  return <p role="alert">Componente no compatible.</p>;
}
