import { useEffect, useState } from "react";
import { useAdminRequest, adminError } from "./adminClient";
import { useAuth } from "./AuthContext";
import "./AuthorizationPages.css";
import "./ObservabilityPage.css";

type Health = { status: string; checkedAt: string; checks: { component: string; status: string; code: string }[] };
type Snapshot = {
  health: Health; metrics: { name: string; kind: string; value: number; count: number }[];
  jobs: { pending: number; running: number; failed: number; deadLettered: number } | null;
  events: { pending: number; processing: number; retrying: number; deadLettered: number } | null;
  storage: { provider: string };
  workers: { workerId: string; status: string; lastHeartbeat: string | null; capabilities: { contractId: string; contractVersion: string }[]; runningExecutions: number }[];
};

export function ObservabilityPage() {
  const request = useAdminRequest(); const { request: healthRequest } = useAuth();
  const [healthStates, setHealthStates] = useState<{ live: string; ready: string } | null>(null);
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    void Promise.all([
      request("/api/platform/observability", { signal: controller.signal }).then(response => response.json() as Promise<Snapshot>),
      healthRequest("/api/health/live", { signal: controller.signal }).then(response => response.json() as Promise<{ status: string }>),
      // Readiness uses HTTP 503 for a valid Unhealthy snapshot, rather than an API failure.
      healthRequest("/api/health/ready", { signal: controller.signal }).then(response => response.json() as Promise<{ status: string }>),
    ]).then(([data, live, ready]) => {
      if (!controller.signal.aborted) { setSnapshot(data); setHealthStates({ live: live.status ?? "No disponible", ready: ready.status ?? "No disponible" }); }
    }).catch(cause => { if (!controller.signal.aborted) setError(adminError(cause)); });
    return () => controller.abort();
  }, [request, healthRequest, refresh]);
  return <section className="authorization-content observability-page"><h1>Observabilidad</h1><button type="button" onClick={() => setRefresh(value => value + 1)}>Actualizar</button>
    <p>Métricas de esta ejecución. Los contadores se reinician al reiniciar el servidor.</p>
    {error && <p role="alert">{error}</p>}
    {!snapshot && !error && <p role="status">Cargando observabilidad…</p>}
    {snapshot && <>
      <h2>Liveness / Readiness</h2><p>Liveness: {healthStates?.live ?? "No disponible"}; Readiness: {healthStates?.ready ?? "No disponible"}.</p>
      <h2>Health: {snapshot.health.status}</h2><p>Comprobado: {new Date(snapshot.health.checkedAt).toLocaleString()}</p>
      <table><caption>Servicios y stores</caption><thead><tr><th>Componente</th><th>Estado</th><th>Motivo</th></tr></thead><tbody>{snapshot.health.checks.map(check => <tr key={check.component}><td>{check.component}</td><td>{check.status}</td><td>{check.code}</td></tr>)}</tbody></table>
      <h2>Backlog</h2><p>Jobs pendientes: {snapshot.jobs?.pending ?? "No disponible"}; ejecutándose: {snapshot.jobs?.running ?? "No disponible"}; fallidos: {snapshot.jobs?.failed ?? "No disponible"}; dead letters: {snapshot.jobs?.deadLettered ?? "No disponible"}.</p>
      <p>Events pendientes: {snapshot.events?.pending ?? "No disponible"}; procesándose: {snapshot.events?.processing ?? "No disponible"}; reintentándose: {snapshot.events?.retrying ?? "No disponible"}; dead letters: {snapshot.events?.deadLettered ?? "No disponible"}.</p>
      <h2>Storage</h2><p>Proveedor: {snapshot.storage.provider}</p>
      <table><caption>Workers</caption><thead><tr><th>Worker</th><th>Estado</th><th>Último heartbeat</th><th>Capabilities</th><th>Ejecuciones</th></tr></thead><tbody>{snapshot.workers.map(worker => <tr key={worker.workerId}><td>{worker.workerId}</td><td>{worker.status}</td><td>{worker.lastHeartbeat ? new Date(worker.lastHeartbeat).toLocaleString() : "—"}</td><td>{worker.capabilities.map(capability => `${capability.contractId}@${capability.contractVersion}`).join(", ")}</td><td>{worker.runningExecutions}</td></tr>)}</tbody></table>
      {snapshot.workers.length === 0 && <p>No hay workers registrados.</p>}
      <table><caption>Métricas principales</caption><thead><tr><th>Métrica</th><th>Tipo</th><th>Valor</th></tr></thead><tbody>{snapshot.metrics.map(metric => <tr key={metric.name}><td>{metric.name}</td><td>{metric.kind}</td><td>{metric.value}</td></tr>)}</tbody></table>
    </>}
  </section>;
}
