import { useCallback, useEffect, useState, type FormEvent } from "react";
import { isUserEnabled, useAuth, type AuthUser } from "./AuthContext";
import "./AuthorizationPages.css";
import { useAdminRequest, adminError } from "./adminClient";

type Role = { id: { value: string }; name: string; description: string; createdAt: string; updatedAt: string };
type UserRow = { user: AuthUser; roles: Role[] };
type RoleRow = { role: Role; permissions: string[] };

type JobRow = { jobId: string; jobType: string; status: string; createdAt: string; scheduledAt: string; attemptCount: number; maxAttempts: number; executor: string; executionPolicy?: string; workerId?: string | null; capability?: string | null; generation?: number | null; errorSummary?: string | null };
type JobAttempt = { attemptNumber: number; startedAt: string; completedAt?: string | null; outcome: string; errorSummary?: string | null; executor: string; executorKind?: string; workerId?: string | null; capability?: string | null; generation?: number | null };
export function AdminJobsPage() {
  const { hasPermission } = useAuth(); const request = useAdminRequest(); const canManage = hasPermission("platform.jobs.manage");
  const [status, setStatus] = useState("");
  const [rows, setRows] = useState<JobRow[]>([]); const [selected, setSelected] = useState<string | null>(null); const [attempts, setAttempts] = useState<JobAttempt[]>([]);
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(true);
  const load = useCallback(async () => { setBusy(true); setError(null); try { const response = await request(`/api/platform/jobs?limit=200${status ? `&status=${status}` : ""}`); setRows(await response.json() as JobRow[]); } catch (cause) { setError(adminError(cause)); } finally { setBusy(false); } }, [request, status]);
  useEffect(() => { void load(); }, [load]);
  const action = async (id: string, name: "cancel" | "retry") => { try { await request(`/api/platform/jobs/${id}/${name}`, { method: "POST" });  await load(); } catch (cause) { setError(adminError(cause)); } };
  const showAttempts = async (id: string) => { try { const response = await request(`/api/platform/jobs/${id}/attempts`); setSelected(id); setAttempts(await response.json() as JobAttempt[]); } catch (cause) { setError(adminError(cause)); } };
  return <section className="authorization-content"><p className="auth-eyebrow">PLATFORM · BACKGROUND JOBS</p><h1>Background Jobs</h1><p className="auth-lede">Últimos 200 Jobs, ejecutados en local o en WorkerAgent. Los payloads no se muestran en esta lista.</p><button type="button" disabled={busy} onClick={() => void load()}>Actualizar</button><label>Estado del Job<select value={status} onChange={event => setStatus(event.target.value)}><option value="">Todos</option>{["Pending", "Running", "Succeeded", "Failed", "Cancelled", "DeadLettered"].map(value => <option key={value}>{value}</option>)}</select></label>{error && <p className="auth-error" role="alert">{error}</p>}{busy && <p role="status">Cargando Jobs…</p>}{!busy && <div className="auth-table-wrap"><table><thead><tr><th>Job</th><th>Estado</th><th>Creado / programado</th><th>Intentos</th><th>Executor / worker</th><th>Policy / capability</th><th>Error</th><th>Acciones</th></tr></thead><tbody>{rows.map(job => <tr key={job.jobId}><td>{job.jobType}<small>{job.jobId}</small></td><td><span className="admin-badge">{job.status}</span></td><td>{new Date(job.createdAt).toLocaleString()}<small>{new Date(job.scheduledAt).toLocaleString()}</small></td><td>{job.attemptCount}/{job.maxAttempts}</td><td>{job.executor}{job.workerId && <small>{job.workerId}</small>}{job.generation != null && <small>generation {job.generation}</small>}</td><td>{job.executionPolicy ?? "LocalOnly"}{job.capability && <small>{job.capability}</small>}</td><td>{job.errorSummary ? "Error registrado" : "—"}</td><td><button type="button" className="auth-secondary" onClick={() => void showAttempts(job.jobId)}>Intentos</button>{canManage && job.status === "Pending" && <button type="button" className="auth-secondary" onClick={() => void action(job.jobId, "cancel")}>Cancelar</button>}{canManage && ["Failed", "DeadLettered"].includes(job.status) && <button type="button" className="auth-secondary" onClick={() => void action(job.jobId, "retry")}>Reintentar</button>}</td></tr>)}{rows.length === 0 && <tr><td colSpan={8}>No hay Jobs.</td></tr>}</tbody></table></div>}{selected && <section aria-label="Historial de intentos"><h2>Intentos · {selected}</h2><ul>{attempts.map(attempt => <li key={attempt.attemptNumber}>#{attempt.attemptNumber} · {attempt.outcome} · {attempt.executorKind ?? attempt.executor}{attempt.workerId && ` · worker ${attempt.workerId}`}{attempt.capability && ` · ${attempt.capability}`}{attempt.generation != null && ` · generation ${attempt.generation}`} · {attempt.errorSummary ? "Error registrado" : "sin error"}</li>)}</ul></section>}</section>;
}

type DeliveryRow = { deliveryId: string; eventId: string; eventType: string; subscriberId: string; status: string; attemptCount: number; nextAttemptAt: string; lastError?: string | null };
export function AdminEventsPage() {
  const { hasPermission } = useAuth(); const request = useAdminRequest(); const canManage = hasPermission("platform.events.manage");
  const [status, setStatus] = useState("");
  const [attempts, setAttempts] = useState<{ attemptNumber: number; finishedAt: string; outcome: string | number }[]>([]); const [selected, setSelected] = useState<string | null>(null);
  const [rows, setRows] = useState<DeliveryRow[]>([]); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(true);
  const load = useCallback(async () => { setBusy(true); setError(null); try { const response = await request(`/internal/admin/events/deliveries?limit=200${status ? `&status=${status === "Retrying" ? "Pending" : status}` : ""}`); const deliveries = await response.json() as DeliveryRow[]; setRows(status === "Retrying" ? deliveries.filter(row => row.attemptCount > 0) : deliveries); } catch (cause) { setError(adminError(cause)); } finally { setBusy(false); } }, [request, status]);
  useEffect(() => { void load(); }, [load]);
  const replay = async (id: string) => { try { await request(`/internal/admin/events/deliveries/${id}/replay`, { method: "POST" }); await load(); } catch (cause) { setError(adminError(cause)); } };
  const showAttempts = async (id: string) => { try { const response = await request(`/internal/admin/events/deliveries/${id}/attempts`); setAttempts(await response.json()); setSelected(id); } catch (cause) { setError(adminError(cause)); } };
  return <section className="authorization-content"><p className="auth-eyebrow">PLATFORM · EVENTOS</p><h1>Entregas de eventos</h1><p className="auth-lede">Últimas 200 entregas por subscriber: estado, reintentos y dead-letter.</p><button type="button" disabled={busy} onClick={() => void load()}>Actualizar</button><label>Estado de entrega<select value={status} onChange={event => setStatus(event.target.value)}><option value="">Todos</option>{["Pending", "Processing", "Retrying", "Delivered", "DeadLettered"].map(value => <option key={value}>{value}</option>)}</select></label>{error && <p className="auth-error" role="alert">{error}</p>}{busy && <p role="status">Cargando entregas…</p>}{!busy && <div className="auth-table-wrap"><table><thead><tr><th>Evento</th><th>Subscriber</th><th>Estado</th><th>Intentos</th><th>Último error</th><th>Acción</th></tr></thead><tbody>{rows.map(row => <tr key={row.deliveryId}><td>{row.eventType}<small>{row.eventId}</small></td><td>{row.subscriberId}</td><td><span className="admin-badge">{row.status}</span></td><td>{row.attemptCount}</td><td>{row.lastError ? "Error registrado" : "—"}</td><td>{canManage && <button type="button" className="auth-secondary" onClick={() => void showAttempts(row.deliveryId)}>Intentos</button>}{canManage && row.status === "DeadLettered" && <button type="button" className="auth-secondary" onClick={() => void replay(row.deliveryId)}>Reproducir</button>}</td></tr>)}{rows.length === 0 && <tr><td colSpan={6}>No hay entregas.</td></tr>}</tbody></table></div>}{selected && <section aria-label="Intentos de entrega"><h2>Intentos · {selected}</h2><ul>{attempts.map(attempt => <li key={attempt.attemptNumber}>#{attempt.attemptNumber} · {typeof attempt.outcome === "number" ? ["Pending", "Processing", "Delivered", "DeadLettered"][attempt.outcome] : attempt.outcome} · {new Date(attempt.finishedAt).toLocaleString()}</li>)}</ul>{attempts.length === 0 && <p>No hay intentos.</p>}</section>}</section>;
}

export function AdminUsersPage() {
  const { hasPermission } = useAuth(); const request = useAdminRequest();
  const canWrite = hasPermission("platform.users.write");
  const canReadRoles = hasPermission("platform.roles.read");
  const [users, setUsers] = useState<UserRow[]>([]);
  const [roles, setRoles] = useState<RoleRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(true);
  const load = useCallback(async () => {
    setBusy(true); setError(null);
    try {
      const [userResponse, roleResponse] = await Promise.all([request("/api/authorization/users"), canReadRoles ? request("/api/authorization/roles") : Promise.resolve(null)]);
      setUsers(await userResponse.json() as UserRow[]); setRoles(roleResponse ? await roleResponse.json() as RoleRow[] : []);
    } catch (cause) { setError(adminError(cause)); }
    finally { setBusy(false); }
  }, [request, canReadRoles]);
  useEffect(() => { void load(); }, [load]);

  const update = async (path: string, init: RequestInit) => {
    await request(path, init);
    await load();
  };

  return <section className="authorization-content"><p className="auth-eyebrow">PLATFORM · CONTROL DE ACCESO</p><h1>Usuarios</h1><p className="auth-lede">Consulta el estado y los roles asignados a cada cuenta.</p><button type="button" disabled={busy} onClick={() => void load()}>Actualizar</button>
    {error && <p className="auth-error" role="alert">{error}</p>}{busy && <p role="status">Cargando usuarios…</p>}
    {!busy && <div className="auth-table-wrap"><table><thead><tr><th>Usuario</th><th>Estado</th><th>Roles</th><th>Acciones</th></tr></thead><tbody>
      {users.map(row => { const enabled = isUserEnabled(row.user); return <tr key={row.user.id.value}><td><strong>{row.user.displayName}</strong><small>{row.user.username}</small></td><td><span className="admin-badge">{enabled ? "Activo" : "Deshabilitado"}</span></td><td><div className="auth-tags">{row.roles.map(role => <span key={role.id.value}>{role.name}{canWrite && <button type="button" aria-label={`Quitar rol ${role.name} a ${row.user.username}`} onClick={() => void update(`/api/authorization/users/${row.user.id.value}/roles/${role.id.value}`, { method: "DELETE" }).catch(cause => setError(adminError(cause)))}>×</button>}</span>)}</div>{canWrite && canReadRoles && <select aria-label={`Asignar rol a ${row.user.username}`} defaultValue="" onChange={event => { const id = event.target.value; event.target.value = ""; if (id) void update(`/api/authorization/users/${row.user.id.value}/roles/${id}`, { method: "POST" }).catch(cause => setError(adminError(cause))); }}><option value="">Asignar rol…</option>{roles.filter(item => !row.roles.some(role => role.id.value === item.role.id.value)).map(item => <option key={item.role.id.value} value={item.role.id.value}>{item.role.name}</option>)}</select>}</td><td>{canWrite && <button type="button" className="auth-secondary" onClick={() => void update(`/api/authorization/users/${row.user.id.value}/status`, { method: "PATCH", body: JSON.stringify({ enabled: !enabled }) }).catch(cause => setError(adminError(cause)))}>{enabled ? "Deshabilitar" : "Habilitar"}</button>}</td></tr>; })}
      {users.length === 0 && <tr><td colSpan={4}>No hay usuarios.</td></tr>}
    </tbody></table></div>}
  </section>;
}

export function AdminRolesPage() {
  const { hasPermission } = useAuth(); const request = useAdminRequest();
  const [roles, setRoles] = useState<RoleRow[]>([]);
  const [name, setName] = useState(""); const [description, setDescription] = useState(""); const [permission, setPermission] = useState("");
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(true);
  const load = useCallback(async () => { setBusy(true); setError(null); try { const response = await request("/api/authorization/roles"); setRoles(await response.json() as RoleRow[]); } catch (cause) { setError(adminError(cause)); } finally { setBusy(false); } }, [request]);
  useEffect(() => { void load(); }, [load]);
  const saveRole = async (event: FormEvent) => { try { event.preventDefault(); await request("/api/authorization/roles", { method: "POST", body: JSON.stringify({ name, description }) }); setName(""); setDescription(""); await load(); } catch (cause) { setError(adminError(cause)); } };
  const updateRole = async (role: Role, nextName: string, nextDescription: string) => { try { await request(`/api/authorization/roles/${role.id.value}`, { method: "PUT", body: JSON.stringify({ name: nextName, description: nextDescription }) }); await load(); } catch (cause) { setError(adminError(cause)); } };
  const changePermission = async (roleId: string, permissionId: string, add: boolean) => { try { await request(`/api/authorization/roles/${roleId}/permissions/${encodeURIComponent(permissionId)}`, { method: add ? "PUT" : "DELETE" }); await load(); } catch (cause) { setError(adminError(cause)); } };

  return <section className="authorization-content"><p className="auth-eyebrow">PLATFORM · CONTROL DE ACCESO</p><h1>Roles</h1><p className="auth-lede">Los permisos son identificadores versionables; por ejemplo, <code>platform.audit.read</code>.</p>
    {error && <p className="auth-error" role="alert">{error}</p>}
    {hasPermission("platform.roles.write") && <form className="auth-create-role" onSubmit={saveRole}><h2>Crear rol</h2><label>Nombre<input required minLength={3} maxLength={64} value={name} onChange={event => setName(event.target.value)} /></label><label>Descripción<input maxLength={500} value={description} onChange={event => setDescription(event.target.value)} /></label><button type="submit">Crear</button></form>}
    <button type="button" disabled={busy} onClick={() => void load()}>Actualizar</button>{busy && <p role="status">Cargando roles…</p>}
    {!busy && roles.length === 0 && <p>No hay roles.</p>}<div className="auth-role-list">{roles.map(({ role, permissions }) => <RoleCard key={role.id.value} role={role} permissions={permissions} canEdit={hasPermission("platform.roles.write")} canManagePermissions={hasPermission("platform.roles.permissions.write")} onSave={(nextName, nextDescription) => void updateRole(role, nextName, nextDescription)} onPermission={(id, add) => void changePermission(role.id.value, id, add)} permission={permission} setPermission={setPermission} />)}</div>
  </section>;
}

function RoleCard({ role, permissions, canEdit, canManagePermissions, onSave, onPermission, permission, setPermission }: { role: Role; permissions: string[]; canEdit: boolean; canManagePermissions: boolean; onSave: (name: string, description: string) => void; onPermission: (permission: string, add: boolean) => void; permission: string; setPermission: (value: string) => void }) {
  const [name, setName] = useState(role.name); const [description, setDescription] = useState(role.description);
  return <article className="auth-role-card"><h2>{role.name}</h2><div className="auth-role-heading"><div><label>Nombre<input value={name} disabled={!canEdit || role.name === "administrator"} onChange={event => setName(event.target.value)} /></label><label>Descripción<input value={description} disabled={!canEdit} onChange={event => setDescription(event.target.value)} /></label></div>{canEdit && <button type="button" className="auth-secondary" onClick={() => onSave(name, description)}>Guardar</button>}</div>
    <h3>Permisos</h3><div className="auth-tags">{permissions.map(value => <span key={value}>{value}{canManagePermissions && value !== "platform.admin" && <button type="button" aria-label={`Quitar permiso ${value}`} onClick={() => onPermission(value, false)}>×</button>}</span>)}</div>
    {canManagePermissions && <form className="auth-permission-form" onSubmit={event => { event.preventDefault(); const value = permission.trim(); if (value) onPermission(value, true); setPermission(""); }}><label>Añadir permiso<input value={permission} onChange={event => setPermission(event.target.value)} placeholder="ej. platform.audit.read" /></label><button type="submit">Añadir</button></form>}
  </article>;
}
