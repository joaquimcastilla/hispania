import { useEffect, useState } from "react";
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
  const { request } = useAuth();
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [error, setError] = useState(false);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setError(false);
    void request("/api/platform/observability", { signal: controller.signal }).then(async response => {
      if (!response.ok) throw new Error("unavailable");
      const data: Snapshot = await response.json();
      if (!controller.signal.aborted) setSnapshot(data);
    }).catch(() => { if (!controller.signal.aborted) setError(true); });
    return () => controller.abort();
  }, [request, refresh]);
  return <main className="authorization-shell observability-page"><h1>Observabilidad</h1><a href="/">Volver a HISPANIA</a><button type="button" onClick={() => setRefresh(value => value + 1)}>Actualizar</button>
    <p>Métricas de esta ejecución. Los contadores se reinician al reiniciar el servidor.</p>
    {error && <p role="alert">No se pudo actualizar la observabilidad.</p>}
    {!snapshot && !error && <p role="status">Cargando observabilidad…</p>}
    {snapshot && <>
      <h2>Health: {snapshot.health.status}</h2><p>Comprobado: {new Date(snapshot.health.checkedAt).toLocaleString()}</p>
      <table><caption>Servicios y stores</caption><thead><tr><th>Componente</th><th>Estado</th><th>Motivo</th></tr></thead><tbody>{snapshot.health.checks.map(check => <tr key={check.component}><td>{check.component}</td><td>{check.status}</td><td>{check.code}</td></tr>)}</tbody></table>
      <h2>Backlog</h2><p>Jobs pendientes: {snapshot.jobs?.pending ?? "No disponible"}; ejecutándose: {snapshot.jobs?.running ?? "No disponible"}; fallidos: {snapshot.jobs?.failed ?? "No disponible"}; dead letters: {snapshot.jobs?.deadLettered ?? "No disponible"}.</p>
      <p>Events pendientes: {snapshot.events?.pending ?? "No disponible"}; procesándose: {snapshot.events?.processing ?? "No disponible"}; reintentándose: {snapshot.events?.retrying ?? "No disponible"}; dead letters: {snapshot.events?.deadLettered ?? "No disponible"}.</p>
      <h2>Storage</h2><p>Proveedor: {snapshot.storage.provider}</p>
      <table><caption>Workers</caption><thead><tr><th>Worker</th><th>Estado</th><th>Último heartbeat</th><th>Capabilities</th><th>Ejecuciones</th></tr></thead><tbody>{snapshot.workers.map(worker => <tr key={worker.workerId}><td>{worker.workerId}</td><td>{worker.status}</td><td>{worker.lastHeartbeat ? new Date(worker.lastHeartbeat).toLocaleString() : "—"}</td><td>{worker.capabilities.map(capability => `${capability.contractId}@${capability.contractVersion}`).join(", ")}</td><td>{worker.runningExecutions}</td></tr>)}</tbody></table>
      {snapshot.workers.length === 0 && <p>No hay workers registrados.</p>}
      <table><caption>Métricas principales</caption><thead><tr><th>Métrica</th><th>Tipo</th><th>Valor</th></tr></thead><tbody>{snapshot.metrics.map(metric => <tr key={metric.name}><td>{metric.name}</td><td>{metric.kind}</td><td>{metric.value}</td></tr>)}</tbody></table>
    </>}
  </main>;
}
