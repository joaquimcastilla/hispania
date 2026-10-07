import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { App } from "./App";
import { adminRoutes } from "./webModules";

const response = (body: unknown, status = 200) => ({ ok: status < 400, status, json: async () => body } as Response);
function setup(path: string, permissions: string[], handler: (path: string, init?: RequestInit) => Response = () => response([])) {
  window.sessionStorage.setItem("hispania.identity.session", "test-session"); window.history.replaceState({}, "", path);
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => String(input) === "/api/identity/me"
    ? response({ user: { displayName: "Operator" }, effectivePermissions: permissions }) : handler(String(input), init));
  vi.stubGlobal("fetch", fetch); return fetch;
}
afterEach(() => { vi.unstubAllGlobals(); window.sessionStorage.clear(); window.history.replaceState({}, "", "/"); });

it("hides Admin for a domain user while keeping the catalog usable", async () => {
  setup("/", ["media.library.read"]); render(<App />);
  expect(await screen.findByRole("heading", { name: "Películas" })).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "Admin" })).not.toBeInTheDocument();
});
it.each(adminRoutes)("protects direct $path without administrative grants", async route => {
  const fetch = setup(route.path, ["media.library.read"]); render(<App />);
  expect(await screen.findByRole("heading", { name: "Acceso denegado" })).toBeInTheDocument(); expect(fetch.mock.calls.filter(call => String(call[0]) !== "/api/v1/web/contributions")).toHaveLength(1);
});
it("opens only Jobs for a partial administrator entering /admin", async () => {
  const fetch = setup("/admin", ["platform.jobs.read"]); render(<App variant="classic" />);
  expect(await screen.findByRole("heading", { name: "Background Jobs" })).toBeInTheDocument();
  const nav = within(screen.getByRole("navigation", { name: "Administración" }));
  expect(nav.getAllByRole("link")).toHaveLength(1); expect(nav.getByRole("link", { name: "Jobs" })).toHaveAttribute("aria-current", "page");
  expect(window.location.pathname).toBe("/admin/jobs"); expect(fetch.mock.calls.filter(call => String(call[0]) === "/api/identity/me")).toHaveLength(1);
});
it.each([
  ["modern", "platform.events.read", "Eventos", "/admin/events", "Entregas de eventos"],
  ["classic", "platform.events.read", "Eventos", "/admin/events", "Entregas de eventos"],
  ["modern", "platform.users.read", "Usuarios", "/admin/users", "Usuarios"],
  ["classic", "platform.users.read", "Usuarios", "/admin/users", "Usuarios"],
] as const)("isolates %s %s navigation and controls", async (variant, permission, label, path, heading) => {
  const fetch = setup("/admin", [permission]); render(<App variant={variant} />);
  expect(await screen.findByRole("heading", { name: heading })).toBeInTheDocument();
  const nav = within(screen.getByRole("navigation", { name: "Administración" }));
  expect(nav.getAllByRole("link")).toHaveLength(1); expect(nav.getByRole("link", { name: label })).toHaveAttribute("aria-current", "page");
  expect(window.location.pathname).toBe(path);
  for (const name of ["Reproducir", "Habilitar", "Deshabilitar", "Crear", "Cancelar", "Reintentar"]) expect(screen.queryByRole("button", { name })).not.toBeInTheDocument();
  expect(fetch.mock.calls.filter(call => String(call[0]) === "/api/identity/me")).toHaveLength(1);
  expect(fetch.mock.calls.some(call => String(call[0]) === "/api/authorization/roles")).toBe(false);
});
it.each([false, true])("gates event attempts/replay and filters retrying deliveries (%s)", async manage => {
  const fetch = setup("/admin/events", ["platform.events.read", ...(manage ? ["platform.events.manage"] : [])], path => {
    if (path.endsWith("/attempts")) return response([{ attemptNumber: 1, outcome: "DeadLettered", finishedAt: "2026-10-04T12:00:00Z", error: "private event failure" }]);
    if (path.endsWith("/replay")) return response(null, 202);
    return response([{ deliveryId: "delivery1", eventId: "event1", eventType: "test.event", subscriberId: "subscriber", status: "DeadLettered", attemptCount: 1, lastError: "private event failure" }]);
  });
  render(<App />); await screen.findByText("test.event"); const actor = userEvent.setup();
  if (manage) {
    await actor.click(screen.getByRole("button", { name: "Intentos" })); expect(await screen.findByText(/#1 · DeadLettered/)).toBeInTheDocument();
    await actor.click(screen.getByRole("button", { name: "Reproducir" })); await waitFor(() => expect(fetch.mock.calls.some(call => String(call[0]).endsWith("/replay"))).toBe(true));
  } else { expect(screen.queryByRole("button", { name: "Reproducir" })).not.toBeInTheDocument(); expect(screen.queryByRole("button", { name: "Intentos" })).not.toBeInTheDocument(); }
  expect(screen.queryByText("private event failure")).not.toBeInTheDocument();
  await actor.selectOptions(screen.getByLabelText("Estado de entrega"), "Retrying");
  await waitFor(() => expect(fetch.mock.calls.some(call => String(call[0]) === "/internal/admin/events/deliveries?limit=200&status=Pending")).toBe(true));
});
it.each([false, true])("gates user status mutations independently from reading (%s)", async write => {
  const fetch = setup("/admin/users", ["platform.users.read", ...(write ? ["platform.users.write"] : [])], () => response([{ user: { id: { value: "user1" }, username: "user1", displayName: "User One", status: "Enabled" }, roles: [{ id: { value: "role1" }, name: "reader" }] }]));
  render(<App />); await screen.findByText("User One");
  if (write) { await userEvent.setup().click(screen.getByRole("button", { name: "Deshabilitar" })); await waitFor(() => expect(fetch.mock.calls.some(call => String(call[0]) === "/api/authorization/users/user1/status" && (call[1] as RequestInit).method === "PATCH")).toBe(true)); }
  else { expect(screen.queryByRole("button", { name: "Deshabilitar" })).not.toBeInTheDocument(); expect(screen.queryByRole("button", { name: /Quitar rol/ })).not.toBeInTheDocument(); }
});
it("offers an Admin entry when an operator has no domain landing-page grant", async () => {
  setup("/", ["platform.jobs.read"]); render(<App />);
  expect(await screen.findByRole("link", { name: "Admin" })).toHaveAttribute("href", "/admin/jobs");
});
it("keeps the Admin entry in the mobile drawer", async () => {
  setup("/", ["media.library.read", "platform.events.read"]); render(<App />);
  await screen.findByRole("heading", { name: "Películas" }); await userEvent.setup().click(screen.getByRole("button", { name: "Abrir menú" }));
  expect(within(screen.getByRole("dialog")).getByRole("link", { name: "Admin" })).toHaveAttribute("href", "/admin/events");
});
it("renders one overview snapshot and registered navigation for an administrator", async () => {
  const fetch = setup("/admin", ["platform.admin"], () => response({ status: "Healthy", checkedAt: "2026-10-04T12:00:00Z", activeUsers: 2, activeSessions: 3, pendingJobs: 4, deadLetterEvents: 0, connectedWorkers: 1, storageStatus: "Healthy", modules: { total: 5, healthy: 5 } }));
  render(<App />); expect(await screen.findByText("5/5")).toBeInTheDocument();
  const nav = within(screen.getByRole("navigation", { name: "Administración" }));
  for (const route of adminRoutes) expect(nav.getByRole("link", { name: route.label })).toHaveAttribute("href", route.path);
  expect(fetch.mock.calls.filter(call => String(call[0]) !== "/api/v1/web/contributions")).toHaveLength(2); await userEvent.setup().click(screen.getByRole("button", { name: "Actualizar" })); await waitFor(() => expect(fetch.mock.calls.filter(call => String(call[0]) !== "/api/v1/web/contributions")).toHaveLength(3));
});
it.each([false, true])("separates job read from manage (%s) and keeps conflict errors visible", async manage => {
  setup("/admin/jobs", ["platform.jobs.read", ...(manage ? ["platform.jobs.manage"] : [])], (path, init) => init?.method === "POST" ? response({ error: "private exception /private/path" }, 409) : response([{ jobId: "job1", jobType: "test", status: "Pending", createdAt: "2026-10-04", scheduledAt: "2026-10-04", attemptCount: 0, maxAttempts: 3, executor: "local" }]));
  render(<App />); await screen.findByText("test");
  if (!manage) expect(screen.queryByRole("button", { name: "Cancelar" })).not.toBeInTheDocument();
  else { await userEvent.setup().click(screen.getByRole("button", { name: "Cancelar" })); expect(await screen.findByRole("alert")).toHaveTextContent("conflicto"); expect(screen.queryByText(/private exception/)).not.toBeInTheDocument(); }
});
it.each([403, 404, 409, 500])("maps HTTP %s safely", async status => {
  setup("/admin/jobs", ["platform.jobs.read"], () => response({ error: "raw exception" }, status)); render(<App />);
  expect(await screen.findByRole("alert")).toHaveTextContent(status === 403 ? "permiso" : status === 404 ? "no existe" : status === 409 ? "conflicto" : "servidor");
  expect(screen.queryByText("raw exception")).not.toBeInTheDocument();
});
it("clears an expired session and preserves the return route at login", async () => {
  setup("/admin/jobs", ["platform.jobs.read"], () => response({}, 401)); render(<App />);
  expect(await screen.findByRole("heading", { name: "Iniciar sesión" })).toBeInTheDocument(); expect(window.sessionStorage.getItem("hispania.identity.session")).toBeNull();
  expect(window.location.pathname).toBe("/login"); expect(new URLSearchParams(window.location.search).get("returnTo")).toBe("/admin/jobs");
});
it("removes previously loaded administrative data after a refresh returns 401", async () => {
  let revoked = false;
  setup("/admin/jobs", ["platform.jobs.read"], () => revoked ? response({}, 401) : response([{ jobId: "job1", jobType: "loaded-admin-data", status: "Pending", createdAt: "2026-10-04", scheduledAt: "2026-10-04", attemptCount: 0, maxAttempts: 3, executor: "local" }]));
  render(<App />); expect(await screen.findByText("loaded-admin-data")).toBeInTheDocument(); revoked = true;
  await userEvent.setup().click(screen.getByRole("button", { name: "Actualizar" }));
  expect(await screen.findByRole("heading", { name: "Iniciar sesión" })).toBeInTheDocument();
  expect(screen.queryByText("loaded-admin-data")).not.toBeInTheDocument(); expect(screen.queryByRole("navigation", { name: "Administración" })).not.toBeInTheDocument();
  expect(window.sessionStorage.getItem("hispania.identity.session")).toBeNull();
});
it("shows safe asset metadata and uses the existing audited delete API", async () => {
  const fetch = setup("/admin/assets", ["platform.assets.write"], (path, init) => init?.method === "DELETE" ? response(null, 204) : response({ id: "asset1", type: "Video", mimeType: "video/mp4", size: 12, provider: "filesystem", createdAtUtc: "2026-10-04", updatedAtUtc: "2026-10-04", contentHandle: "private-path" }));
  render(<App />); const actor = userEvent.setup(); await actor.type(await screen.findByLabelText("Asset ID"), "asset1"); await actor.click(screen.getByRole("button", { name: "Consultar" }));
  expect(await screen.findByText("video/mp4")).toBeInTheDocument(); expect(screen.queryByText("private-path")).not.toBeInTheDocument();
  await actor.click(screen.getByRole("button", { name: "Eliminar asset" })); await waitFor(() => expect(screen.queryByText("video/mp4")).not.toBeInTheDocument()); expect(fetch.mock.calls.some(call => String(call[0]) === "/api/assets/asset1")).toBe(true);
});
it("targets an installed module version and permits reactivation of a stopped extension", async () => {
  const rows = [
    { id: "extension", name: "Demo", version: "1.0.0", status: "Stopped", generation: null, healthy: false, executionModel: "in-process", extension: true, compatible: true, loaded: true, installed: true, preferred: false },
    { id: "extension", name: "Demo", version: "2.0.0", status: "Discovered", generation: null, healthy: null, executionModel: "out-of-process", extension: true, compatible: true, loaded: false, installed: true, preferred: true },
  ];
  const fetch = setup("/admin/modules", ["platform.admin"], (path, init) => init?.method === "POST" ? response({ error: "private package path" }, 409) : response(rows));
  render(<App />); const actor = userEvent.setup(); const update = await screen.findByRole("button", { name: "Actualizar a 2.0.0" });
  expect(screen.getAllByRole("button", { name: "Activar" })).toHaveLength(1); await actor.click(update);
  expect(await screen.findByRole("alert")).toHaveTextContent("conflicto"); expect(fetch.mock.calls.some(call => String(call[0]) === "/api/platform/admin/modules/extension/update?version=2.0.0")).toBe(true);
  expect(screen.queryByText("private package path")).not.toBeInTheDocument();
});
