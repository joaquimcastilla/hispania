import { useCallback, useEffect, useState, type FormEvent } from "react";
import { useAuth } from "./AuthContext";
import { adminError, useAdminRequest } from "./adminClient";

type Owner = { owner: { value: string }; capabilities: { maximumBatchSize: number; supportsExecution: boolean } };
type Plan = { planId: string; owner: { value: string }; candidateCount: number; protectedCount: number; evaluatedAtUtc: string; expiresAtUtc: string; reason: string; warnings: string[] };
type Run = { runId: string; owner: { value: string }; state: number; batches: number; candidates: number; affected: number; startedAtUtc: string };
type Capacity = { source: { value: string }; observedAtUtc: string; totalBytes: number | null; freeBytes: number | null; pressure: number; complete: boolean; diagnostic: string | null };
type Finding = { findingId: string; kind: number; severity: number; reason: string; quarantined: boolean; subjectId: string; firstObservedUtc: string | null; lastObservedUtc: string | null; suggestedAction: string | null };
type Scan = { complete: boolean; examined: number; findings: Finding[]; continuationToken: string | null };
type QuarantineEntry = { findingId: string; contentHandle: string; reason: string; firstObservedUtc: string };

export function AdminMaintenancePage() {
  const request = useAdminRequest();
  const { hasPermission } = useAuth();
  const canExecute = hasPermission("platform.maintenance.execute");
  const [owners, setOwners] = useState<Owner[]>([]);
  const [runs, setRuns] = useState<Run[]>([]);
  const [ownerId, setOwnerId] = useState("");
  const [ageDays, setAgeDays] = useState(30);
  const [batchSize, setBatchSize] = useState(100);
  const [maximumRetainedCount, setMaximumRetainedCount] = useState("");
  const [plan, setPlan] = useState<Plan | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const [ownerResponse, runResponse] = await Promise.all([
        request("/api/platform/maintenance/owners"), request("/api/platform/maintenance/runs")]);
      const available = await ownerResponse.json() as Owner[];
      setOwners(available);
      setOwnerId(current => current || available[0]?.owner.value || "");
      setRuns(await runResponse.json() as Run[]);
      setError(null);
    } catch (cause) { setError(adminError(cause)); }
  }, [request]);
  useEffect(() => { void load(); }, [load]);

  const createPlan = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setError(null); setPlan(null); setConfirmed(false);
    try {
      const response = await request("/api/platform/maintenance/retention/plans", { method: "POST",
        body: JSON.stringify({ ownerId, enabled: true, minimumAgeDays: ageDays, batchSize,
          maximumRetainedCount: maximumRetainedCount === "" ? null : Number(maximumRetainedCount) }) });
      setPlan(await response.json() as Plan);
    } catch (cause) { setError(adminError(cause)); }
    finally { setBusy(false); }
  };
  const execute = async () => {
    if (!plan || !confirmed) return;
    setBusy(true); setError(null);
    try {
      await request(`/api/platform/maintenance/retention/plans/${plan.planId}/execute`, {
        method: "POST", body: JSON.stringify({ confirm: true, maximumBatches: 1 }) });
      setPlan(null); setConfirmed(false); await load();
    } catch (cause) { setError(adminError(cause)); }
    finally { setBusy(false); }
  };
  return <section className="authorization-content">
    <p className="auth-eyebrow">PLATFORM · MAINTENANCE</p><h1>Retención</h1>
    <p className="auth-lede">La retención automática está desactivada. Revisa un plan antes de ejecutar un lote.</p>
    {error && <p className="auth-error" role="alert">{error}</p>}
    <form className="auth-create-role" onSubmit={event => void createPlan(event)}>
      <h2>Plan de retención</h2>
      <label>Owner<select value={ownerId} onChange={event => { setOwnerId(event.target.value); setPlan(null); }}>
        {owners.map(item => <option key={item.owner.value} value={item.owner.value}>{item.owner.value}</option>)}</select></label>
      <label>Edad mínima, días<input type="number" min="0" max="3650" value={ageDays} onChange={event => setAgeDays(Number(event.target.value))} /></label>
      <label>Tamaño de lote<input type="number" min="1" max={owners.find(item => item.owner.value === ownerId)?.capabilities.maximumBatchSize ?? 500} value={batchSize} onChange={event => setBatchSize(Number(event.target.value))} /></label>
      {ownerId === "platform.backups" && <label>Copias verificadas recientes a conservar<input type="number" min="1" max="1000000" value={maximumRetainedCount} placeholder="2" onChange={event => setMaximumRetainedCount(event.target.value)} /></label>}
      <button type="submit" disabled={busy || !ownerId}>Crear plan</button>
    </form>
    {plan && <section aria-label="Plan de retención"><h2>Plan para {plan.owner.value}</h2>
      <p>{plan.reason}</p><p>Candidatos: {plan.candidateCount}. Protegidos: {plan.protectedCount}.</p>
      <p>Válido hasta {new Date(plan.expiresAtUtc).toLocaleString()}.</p>
      {plan.warnings.map(warning => <p key={warning}>{warning}</p>)}
      {canExecute && <><label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> Confirmo ejecutar un lote de este plan</label>
        <button type="button" disabled={busy || !confirmed || plan.candidateCount === 0} onClick={() => void execute()}>Ejecutar un lote</button></>}
    </section>}
    <h2>Historial</h2><button type="button" disabled={busy} onClick={() => void load()}>Actualizar</button>
    <div className="auth-table-wrap"><table><thead><tr><th>Owner</th><th>Estado</th><th>Lotes</th><th>Afectados</th><th>Inicio</th></tr></thead><tbody>
      {runs.map(run => <tr key={run.runId}><td>{run.owner.value}</td><td>{["Planned", "Running", "Completed", "Failed", "Cancelled", "Interrupted"][run.state] ?? "Unknown"}</td><td>{run.batches}</td><td>{run.affected}</td><td>{new Date(run.startedAtUtc).toLocaleString()}</td></tr>)}
      {runs.length === 0 && <tr><td colSpan={5}>No hay operaciones registradas.</td></tr>}
    </tbody></table></div>
  </section>;
}

export function AdminCapacityPage() {
  const request = useAdminRequest();
  const [snapshots, setSnapshots] = useState<Capacity[]>([]);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { const response = await request("/api/platform/maintenance/capacity"); setSnapshots(await response.json() as Capacity[]); setError(null); }
    catch (cause) { setError(adminError(cause)); }
  }, [request]);
  useEffect(() => { void load(); }, [load]);
  return <section className="authorization-content"><p className="auth-eyebrow">PLATFORM · CAPACITY</p><h1>Capacidad</h1>
    <p className="auth-lede">Espacio observado en los volúmenes configurados. Las mediciones pueden cambiar durante una escritura.</p>
    <button type="button" onClick={() => void load()}>Actualizar</button>{error && <p className="auth-error" role="alert">{error}</p>}
    <div className="auth-table-wrap"><table><thead><tr><th>Origen</th><th>Presión</th><th>Libre</th><th>Total</th><th>Medición</th></tr></thead><tbody>
      {snapshots.map(item => <tr key={item.source.value}><td>{item.source.value}</td><td>{["Desconocida", "Normal", "Advertencia", "Crítica"][item.pressure] ?? "Desconocida"}</td><td>{item.freeBytes == null ? "Desconocido" : `${(item.freeBytes / 1073741824).toFixed(2)} GiB`}</td><td>{item.totalBytes == null ? "Desconocido" : `${(item.totalBytes / 1073741824).toFixed(2)} GiB`}</td><td>{new Date(item.observedAtUtc).toLocaleString()}{!item.complete && <small>{item.diagnostic ?? "Medición parcial"}</small>}</td></tr>)}
      {snapshots.length === 0 && <tr><td colSpan={5}>No hay fuentes de capacidad.</td></tr>}
    </tbody></table></div></section>;
}

export function AdminReconciliationPage() {
  const request = useAdminRequest();
  const [owners, setOwners] = useState<{ value: string }[]>([]);
  const [ownerId, setOwnerId] = useState("");
  const [scan, setScan] = useState<Scan | null>(null);
  const [quarantine, setQuarantine] = useState<QuarantineEntry[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { void (async () => {
    try {
      const response = await request("/api/platform/maintenance/reconciliation/owners");
      const values = await response.json() as { value: string }[];
      setOwners(values); setOwnerId(values[0]?.value ?? "");
    } catch (cause) { setError(adminError(cause)); }
  })(); }, [request]);
  const next = async (cursor: string | null) => {
    setBusy(true); setError(null);
    try {
      const response = await request(`/api/platform/maintenance/reconciliation/${encodeURIComponent(ownerId)}/scan`, {
        method: "POST", body: JSON.stringify({ maximumItems: 100, continuationToken: cursor }) });
      const result = await response.json() as Scan;
      setScan(previous => cursor && previous ? { ...result, findings: [...previous.findings, ...result.findings], examined: previous.examined + result.examined } : result);
    } catch (cause) { setError(adminError(cause)); }
    finally { setBusy(false); }
  };
  const inspectQuarantine = async () => {
    setBusy(true); setError(null);
    try {
      const response = await request(`/api/platform/maintenance/reconciliation/${encodeURIComponent(ownerId)}/quarantine?limit=100`);
      setQuarantine(await response.json() as QuarantineEntry[]);
    } catch (cause) { setError(adminError(cause)); }
    finally { setBusy(false); }
  };
  return <section className="authorization-content"><p className="auth-eyebrow">PLATFORM · RECONCILIATION</p><h1>Reconciliación</h1>
    <p className="auth-lede">Escaneo de Assets y Storage. Los huérfanos permanecen almacenados; los hallazgos ambiguos se ponen en cuarentena lógica tras el período de gracia.</p>
    {error && <p className="auth-error" role="alert">{error}</p>}
    <label>Owner<select value={ownerId} onChange={event => { setOwnerId(event.target.value); setScan(null); setQuarantine([]); }}>
      {owners.map(owner => <option key={owner.value} value={owner.value}>{owner.value}</option>)}</select></label>
    <button type="button" disabled={busy || !ownerId} onClick={() => void next(null)}>Iniciar escaneo</button>
    <button type="button" disabled={busy || !ownerId} onClick={() => void inspectQuarantine()}>Inspeccionar cuarentena</button>
    {quarantine.length > 0 && <section aria-label="Cuarentena lógica"><h2>Cuarentena lógica</h2>
      <div className="auth-table-wrap"><table><thead><tr><th>Contenido</th><th>Razón</th><th>Primera observación</th></tr></thead><tbody>
        {quarantine.map(entry => <tr key={entry.findingId}><td>{entry.contentHandle}</td><td>{entry.reason}</td><td>{new Date(entry.firstObservedUtc).toLocaleString()}</td></tr>)}
      </tbody></table></div></section>}
    {scan && <><p role="status">Examinados: {scan.examined}. {scan.complete ? "Escaneo completo." : "Escaneo parcial."}</p>
      <div className="auth-table-wrap"><table><thead><tr><th>Tipo</th><th>Severidad</th><th>Identificador</th><th>Estado</th></tr></thead><tbody>
        {scan.findings.map(finding => <tr key={finding.findingId}><td>{["MissingContent", "OrphanedContent", "Mismatch", "Unknown"][finding.kind] ?? "Unknown"}</td><td>{["Información", "Advertencia", "Crítica"][finding.severity] ?? "Desconocida"}</td><td>{finding.subjectId}<small>{finding.reason}</small><small>{finding.suggestedAction}</small></td><td>{finding.quarantined ? "Cuarentena lógica" : "Observado"}<small>{finding.firstObservedUtc && new Date(finding.firstObservedUtc).toLocaleString()}</small></td></tr>)}
        {scan.findings.length === 0 && <tr><td colSpan={4}>Sin hallazgos en las páginas examinadas.</td></tr>}
      </tbody></table></div>
      {!scan.complete && <button type="button" disabled={busy || !scan.continuationToken} onClick={() => void next(scan.continuationToken)}>Continuar escaneo</button>}
    </>}
  </section>;
}
