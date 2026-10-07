import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { App } from "./App";
import { validContribution, type WebContribution } from "./DynamicWeb";

const permission = "extensions.test.web.view";
const contribution = (): WebContribution => ({ packageId: "test.web", packageVersion: "1.0.0", contentHash: "A".repeat(64),
  page: { id: "dashboard", route: "/extensions/test.web/dashboard", title: "<img onerror=alert(1)>", permission,
    navigation: { icon: "package", section: "PrimaryNavigation", order: 1 }, components: [
      { type: "text", text: "<script>alert(1)</script>" }, { type: "table", text: "Data", operation: "platform.health" },
      { type: "button", text: "Check", operation: "platform.health.refresh" }] } });
function setup(item = contribution(), permissions = [permission, "platform.observability.read"], status = 200) {
  window.sessionStorage.setItem("hispania.identity.session", "opaque-test-session"); window.history.replaceState({}, "", item.page.route);
  const fetch = vi.fn(async (input: RequestInfo | URL) => {
    const path = String(input);
    const body = path === "/api/identity/me" ? { user: { displayName: "Viewer" }, effectivePermissions: permissions } :
      path === "/api/v1/web/contributions" ? { schemaGeneration: 1, contributions: [item] } :
      { rows: [{ name: "<img onerror=alert(2)>", value: "<script>alert(2)</script>" }], error: "SECRET <script>alert(3)</script> /private/path" };
    return { ok: path.includes("operations") ? status < 400 : true, status: path.includes("operations") ? status : 200, json: async () => body } as Response;
  }); vi.stubGlobal("fetch", fetch); return fetch;
}
afterEach(() => { vi.unstubAllGlobals(); window.sessionStorage.clear(); window.history.replaceState({}, "", "/"); });
it.each(["modern", "classic"] as const)("%s renders manifest and backend strings as text and uses fixed authorized operations", async variant => {
  const fetch = setup(); render(<App variant={variant} />);
  expect(await screen.findByRole("heading", { name: "<img onerror=alert(1)>" })).toBeInTheDocument();
  expect(screen.getByText("<script>alert(1)</script>")).toBeInTheDocument();
  expect(await screen.findByRole("cell", { name: "<script>alert(2)</script>" })).toBeInTheDocument();
  expect(document.querySelector("script,img")).toBeNull();
  await userEvent.setup().click(screen.getByRole("button", { name: "Check" }));
  expect(await screen.findByText("Acción aceptada")).toBeInTheDocument();
  expect(fetch.mock.calls.every(call => String(call[0]).startsWith("/api/"))).toBe(true);
});
it.each([401, 403, 404, 500])("handles operation HTTP %s without exposing error bodies", async status => {
  setup(contribution(), [permission, "platform.observability.read"], status); render(<App />);
  await waitFor(() => expect(screen.queryByText("Cargando contribuciones…")).not.toBeInTheDocument());
  expect(screen.queryByText(/SECRET/)).not.toBeInTheDocument();
});
it("checks direct route permission and isolates invalid contributions", async () => {
  setup(contribution(), []); render(<App />);
  expect(await screen.findByRole("heading", { name: "Página no encontrada" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Check" })).not.toBeInTheDocument();
});
it.each(["javascript:alert(1)", "data:text/html,x", "file:///etc/passwd", "https://example.com", "/admin"])("rejects injected route %s", route => {
  const item = contribution(); item.page.route = route; expect(validContribution(item)).toBe(false);
});
it("rejects future executable components defensively", () => {
  const item = contribution(); item.page.components = [{ type: "script", text: "alert(1)" } as never]; expect(validContribution(item)).toBe(false);
});
