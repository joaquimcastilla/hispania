import { useCallback, useEffect, useState, type FormEvent } from "react";
import { isUserEnabled, useAuth, type AuthUser } from "./AuthContext";
import "./AuthorizationPages.css";

type Role = { id: { value: string }; name: string; description: string; createdAt: string; updatedAt: string };
type UserRow = { user: AuthUser; roles: Role[] };
type RoleRow = { role: Role; permissions: string[] };

export function AuthorizationHeader({ active }: { active: "users" | "roles" | "events" }) {
  const { currentUser, logout, hasPermission } = useAuth();
  const signOut = async () => { await logout(); window.history.replaceState({}, "", "/login"); window.dispatchEvent(new PopStateEvent("popstate")); };
  return <header className="authorization-header"><a className="authorization-brand" href="/">HISPANIA</a><nav aria-label="Administración"><a aria-current={active === "users" ? "page" : undefined} href="/admin/users">Usuarios</a><a aria-current={active === "roles" ? "page" : undefined} href="/admin/roles">Roles</a>{hasPermission("platform.events.read") && <a aria-current={active === "events" ? "page" : undefined} href="/admin/events">Eventos</a>}</nav><span>{currentUser?.user.displayName}</span><button type="button" onClick={() => void signOut()}>Cerrar sesión</button></header>;
}

type DeliveryRow = { deliveryId: string; eventId: string; eventType: string; subscriberId: string; status: string; attemptCount: number; nextAttemptAt: string; lastError?: string | null };
export function AdminEventsPage() {
  const { request, hasPermission } = useAuth(); const canManage = hasPermission("platform.events.manage");
  const [rows, setRows] = useState<DeliveryRow[]>([]); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(true);
  const load = useCallback(async () => { setBusy(true); setError(null); try { const response = await request("/internal/admin/events/deliveries?limit=200"); if (!response.ok) throw new Error("No se pudieron cargar las entregas."); setRows(await response.json() as DeliveryRow[]); } catch (cause) { setError(cause instanceof Error ? cause.message : "Error de conexión."); } finally { setBusy(false); } }, [request]);
  useEffect(() => { void load(); }, [load]);
  const replay = async (id: string) => { const response = await request(`/internal/admin/events/deliveries/${id}/replay`, { method: "POST" }); if (!response.ok) { setError("No se pudo reproducir la entrega."); return; } await load(); };
  return <main className="authorization-admin"><AuthorizationHeader active="events"/><section className="authorization-content"><p className="auth-eyebrow">PLATFORM · EVENTOS</p><h1>Entregas de eventos</h1><p className="auth-lede">Estado por subscriber, intentos y entregas en dead-letter.</p>{error && <p className="auth-error" role="alert">{error}</p>}{busy && <p role="status">Cargando entregas…</p>}{!busy && <div className="auth-table-wrap"><table><thead><tr><th>Evento</th><th>Subscriber</th><th>Estado</th><th>Intentos</th><th>Último error</th><th>Acción</th></tr></thead><tbody>{rows.map(row => <tr key={row.deliveryId}><td>{row.eventType}<small>{row.eventId}</small></td><td>{row.subscriberId}</td><td>{row.status}</td><td>{row.attemptCount}</td><td>{row.lastError ?? "—"}</td><td>{canManage && row.status === "DeadLettered" && <button type="button" className="auth-secondary" onClick={() => void replay(row.deliveryId)}>Reproducir</button>}</td></tr>)}{rows.length === 0 && <tr><td colSpan={6}>No hay entregas.</td></tr>}</tbody></table></div>}</section></main>;
}

export function AdminUsersPage() {
  const { request, hasPermission } = useAuth();
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
      if (!userResponse.ok || (roleResponse && !roleResponse.ok)) throw new Error("No se pudieron cargar los usuarios y roles.");
      setUsers(await userResponse.json() as UserRow[]); setRoles(roleResponse ? await roleResponse.json() as RoleRow[] : []);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Error de conexión."); }
    finally { setBusy(false); }
  }, [request, canReadRoles]);
  useEffect(() => { void load(); }, [load]);

  const update = async (path: string, init: RequestInit) => {
    const response = await request(path, init);
    if (!response.ok) throw new Error(response.status === 403 ? "No tienes permiso para esa operación." : response.status === 409 ? "La operación dejaría la plataforma sin administrador activo o ya existe." : "No se pudo guardar el cambio.");
    await load();
  };

  return <main className="authorization-admin"><AuthorizationHeader active="users"/><section className="authorization-content"><p className="auth-eyebrow">PLATFORM · CONTROL DE ACCESO</p><h1>Usuarios</h1><p className="auth-lede">Consulta el estado y los roles asignados a cada cuenta.</p>
    {error && <p className="auth-error" role="alert">{error}</p>}{busy && <p role="status">Cargando usuarios…</p>}
    {!busy && <div className="auth-table-wrap"><table><thead><tr><th>Usuario</th><th>Estado</th><th>Roles</th><th>Acciones</th></tr></thead><tbody>
      {users.map(row => { const enabled = isUserEnabled(row.user); return <tr key={row.user.id.value}><td><strong>{row.user.displayName}</strong><small>{row.user.username}</small></td><td>{enabled ? "Activo" : "Deshabilitado"}</td><td><div className="auth-tags">{row.roles.map(role => <span key={role.id.value}>{role.name}{canWrite && <button type="button" aria-label={`Quitar rol ${role.name} a ${row.user.username}`} onClick={() => void update(`/api/authorization/users/${row.user.id.value}/roles/${role.id.value}`, { method: "DELETE" }).catch(cause => setError(cause.message))}>×</button>}</span>)}</div>{canWrite && canReadRoles && <select aria-label={`Asignar rol a ${row.user.username}`} defaultValue="" onChange={event => { const id = event.target.value; event.target.value = ""; if (id) void update(`/api/authorization/users/${row.user.id.value}/roles/${id}`, { method: "POST" }).catch(cause => setError(cause.message)); }}><option value="">Asignar rol…</option>{roles.filter(item => !row.roles.some(role => role.id.value === item.role.id.value)).map(item => <option key={item.role.id.value} value={item.role.id.value}>{item.role.name}</option>)}</select>}</td><td>{canWrite && <button type="button" className="auth-secondary" onClick={() => void update(`/api/authorization/users/${row.user.id.value}/status`, { method: "PATCH", body: JSON.stringify({ enabled: !enabled }) }).catch(cause => setError(cause.message))}>{enabled ? "Deshabilitar" : "Habilitar"}</button>}</td></tr>; })}
      {users.length === 0 && <tr><td colSpan={4}>No hay usuarios.</td></tr>}
    </tbody></table></div>}
  </section></main>;
}

export function AdminRolesPage() {
  const { request, hasPermission } = useAuth();
  const [roles, setRoles] = useState<RoleRow[]>([]);
  const [name, setName] = useState(""); const [description, setDescription] = useState(""); const [permission, setPermission] = useState("");
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(true);
  const load = useCallback(async () => { setBusy(true); setError(null); try { const response = await request("/api/authorization/roles"); if (!response.ok) throw new Error("No se pudieron cargar los roles."); setRoles(await response.json() as RoleRow[]); } catch (cause) { setError(cause instanceof Error ? cause.message : "Error de conexión."); } finally { setBusy(false); } }, [request]);
  useEffect(() => { void load(); }, [load]);
  const saveRole = async (event: FormEvent) => { event.preventDefault(); const response = await request("/api/authorization/roles", { method: "POST", body: JSON.stringify({ name, description }) }); if (!response.ok) { setError(response.status === 409 ? "Ya existe un rol con ese nombre." : "No se pudo crear el rol."); return; } setName(""); setDescription(""); await load(); };
  const updateRole = async (role: Role, nextName: string, nextDescription: string) => { const response = await request(`/api/authorization/roles/${role.id.value}`, { method: "PUT", body: JSON.stringify({ name: nextName, description: nextDescription }) }); if (!response.ok) { setError(response.status === 409 ? "Ese nombre está reservado o ya existe." : "No se pudo actualizar el rol."); return; } await load(); };
  const changePermission = async (roleId: string, permissionId: string, add: boolean) => { const response = await request(`/api/authorization/roles/${roleId}/permissions/${encodeURIComponent(permissionId)}`, { method: add ? "PUT" : "DELETE" }); if (!response.ok) { setError("No se pudo cambiar el permiso del rol."); return; } await load(); };

  return <main className="authorization-admin"><AuthorizationHeader active="roles"/><section className="authorization-content"><p className="auth-eyebrow">PLATFORM · CONTROL DE ACCESO</p><h1>Roles</h1><p className="auth-lede">Los permisos son identificadores versionables; por ejemplo, <code>platform.audit.read</code>.</p>
    {error && <p className="auth-error" role="alert">{error}</p>}
    {hasPermission("platform.roles.write") && <form className="auth-create-role" onSubmit={saveRole}><h2>Crear rol</h2><label>Nombre<input required minLength={3} maxLength={64} value={name} onChange={event => setName(event.target.value)} /></label><label>Descripción<input maxLength={500} value={description} onChange={event => setDescription(event.target.value)} /></label><button type="submit">Crear</button></form>}
    {busy && <p role="status">Cargando roles…</p>}
    <div className="auth-role-list">{roles.map(({ role, permissions }) => <RoleCard key={role.id.value} role={role} permissions={permissions} canEdit={hasPermission("platform.roles.write")} canManagePermissions={hasPermission("platform.roles.permissions.write")} onSave={(nextName, nextDescription) => void updateRole(role, nextName, nextDescription)} onPermission={(id, add) => void changePermission(role.id.value, id, add)} permission={permission} setPermission={setPermission} />)}</div>
  </section></main>;
}

function RoleCard({ role, permissions, canEdit, canManagePermissions, onSave, onPermission, permission, setPermission }: { role: Role; permissions: string[]; canEdit: boolean; canManagePermissions: boolean; onSave: (name: string, description: string) => void; onPermission: (permission: string, add: boolean) => void; permission: string; setPermission: (value: string) => void }) {
  const [name, setName] = useState(role.name); const [description, setDescription] = useState(role.description);
  return <article className="auth-role-card"><div className="auth-role-heading"><div><label>Nombre<input value={name} disabled={!canEdit || role.name === "administrator"} onChange={event => setName(event.target.value)} /></label><label>Descripción<input value={description} disabled={!canEdit} onChange={event => setDescription(event.target.value)} /></label></div>{canEdit && <button type="button" className="auth-secondary" onClick={() => onSave(name, description)}>Guardar</button>}</div>
    <h3>Permisos</h3><div className="auth-tags">{permissions.map(value => <span key={value}>{value}{canManagePermissions && value !== "platform.admin" && <button type="button" aria-label={`Quitar permiso ${value}`} onClick={() => onPermission(value, false)}>×</button>}</span>)}</div>
    {canManagePermissions && <form className="auth-permission-form" onSubmit={event => { event.preventDefault(); const value = permission.trim(); if (value) onPermission(value, true); setPermission(""); }}><label>Añadir permiso<input value={permission} onChange={event => setPermission(event.target.value)} placeholder="ej. platform.audit.read" /></label><button type="submit">Añadir</button></form>}
  </article>;
}
