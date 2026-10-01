import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";

const user = { id: { value: "user-1" }, username: "admin", displayName: "Admin", status: "Enabled" };
const session = { id: { value: "session-1" }, userId: user.id, expiresAt: "2030-01-01T00:00:00Z" };
const response = (body: unknown, ok = true, status = 200) => ({ ok, status, json: async () => body } as Response);

afterEach(() => { vi.unstubAllGlobals(); window.sessionStorage.clear(); window.history.replaceState({}, "", "/"); });

describe("Web authentication and protected routes", () => {
  it("logs in, loads current user, and revokes then clears the session on logout", async () => {
    const requests: Array<{ url: string; init?: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input); requests.push({ url, init });
      if (url === "/api/identity/login") return response({ sessionToken: "opaque-session-token" });
      if (url === "/api/identity/me") return response({ user, session, effectivePermissions: ["platform.users.read", "media.library.read", "media.playback.use"] });
      if (url === "/api/identity/logout") return response(null);
      if (url === "/api/v1/media") return response([]);
      throw new Error(`Unexpected request: ${url}`);
    }));
    window.history.replaceState({}, "", "/login");
    const userActor = userEvent.setup(); render(<App />);
    await userActor.type(screen.getByLabelText("Usuario"), "admin");
    await userActor.type(screen.getByLabelText("Contraseña"), "a long enough password");
    await userActor.click(screen.getByRole("button", { name: "Entrar" }));
    await waitFor(() => expect(window.location.pathname).toBe("/"));
    expect(await screen.findByText("Admin")).toBeInTheDocument();
    expect(window.sessionStorage.getItem("hispania.identity.session")).toBe("opaque-session-token");
    expect(screen.getByRole("link", { name: "Usuarios" })).toBeVisible();
    expect(screen.queryByRole("link", { name: "Roles" })).not.toBeInTheDocument();
    await userActor.click(screen.getByRole("button", { name: "Cerrar sesión" }));
    await waitFor(() => expect(window.sessionStorage.getItem("hispania.identity.session")).toBeNull());
    expect(requests.some(item => item.url === "/api/identity/logout")).toBe(true);
  });

  it("redirects direct unauthenticated access to login and hides admin navigation without permission", async () => {
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => String(input) === "/api/identity/me" ? Promise.resolve(response({}, false, 401)) : Promise.resolve(response([]))));
    window.history.replaceState({}, "", "/admin/users"); render(<App />);
    expect(await screen.findByRole("heading", { name: "Iniciar sesión" })).toBeInTheDocument();
    expect(window.location.pathname).toBe("/login");
    window.history.replaceState({}, "", "/");
  });

  it("denies direct access without permission and renders the authorized page", async () => {
    window.sessionStorage.setItem("hispania.identity.session", "opaque-session-token");
    let permissions: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/identity/me") return response({ user, session, effectivePermissions: permissions });
      if (url === "/api/authorization/users" || url === "/api/authorization/roles") return response([]);
      if (url === "/api/v1/media") return response([]);
      throw new Error(`Unexpected request: ${url}`);
    }));
    window.history.replaceState({}, "", "/admin/roles"); const view = render(<App />);
    expect(await screen.findByRole("heading", { name: "Acceso denegado" })).toBeInTheDocument();
    permissions = ["platform.roles.read"];
    view.unmount(); window.history.replaceState({}, "", "/admin/roles"); render(<App />);
    expect(await screen.findByRole("heading", { name: "Roles" })).toBeInTheDocument();
  });

  it("does not treat platform.admin as an implicit Media or Inventory grant", async () => {
    window.sessionStorage.setItem("hispania.identity.session", "opaque-session-token");
    const requests: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input); requests.push(url);
      if (url === "/api/identity/me") return response({ user, session, effectivePermissions: ["platform.admin"] });
      return response([]);
    }));
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Acceso denegado" })).toBeInTheDocument();
    expect(requests).not.toContain("/api/v1/media");
  });
});
