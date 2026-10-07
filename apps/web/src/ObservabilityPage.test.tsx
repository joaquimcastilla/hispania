import { render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { App } from "./App";

const response = (body: unknown) => ({ ok: true, status: 200, json: async () => body } as Response);
afterEach(() => { vi.unstubAllGlobals(); window.sessionStorage.clear(); window.history.replaceState({}, "", "/"); });

it("protects the observability route without fetching administrative details", async () => {
  window.sessionStorage.setItem("hispania.identity.session", "opaque-session");
  window.history.replaceState({}, "", "/admin/observability");
  const fetch = vi.fn(async (input: RequestInfo | URL) => String(input) === "/api/v1/web/contributions" ? response({ schemaGeneration: 1, contributions: [] }) : response({ user: { displayName: "User" }, effectivePermissions: ["media.library.read"] }));
  vi.stubGlobal("fetch", fetch); render(<App />);
  expect(await screen.findByRole("heading", { name: "Acceso denegado" })).toBeInTheDocument();
  expect(fetch.mock.calls.filter(call => String(call[0]) !== "/api/v1/web/contributions")).toHaveLength(1);
});

it("shows safe aggregate observability in the Classic shell using a Session", async () => {
  window.sessionStorage.setItem("hispania.identity.session", "opaque-session");
  window.history.replaceState({}, "", "/admin/observability");
  const fetch = vi.fn(async (input: RequestInfo | URL) => String(input) === "/api/identity/me"
    ? response({ user: { displayName: "Admin" }, effectivePermissions: ["platform.observability.read"] })
    : String(input) === "/api/health/live" ? response({ status: "Healthy" })
    : String(input) === "/api/health/ready" ? ({ ...response({ status: "Unhealthy" }), ok: false, status: 503 } as Response)
    : response({ health: { status: "Degraded", checkedAt: "2026-10-04T12:00:00Z", checks: [{ component: "Events", status: "Degraded", code: "dead_letters" }] }, jobs: { pending: 3, running: 1, failed: 0, deadLettered: 0 }, events: { pending: 2, processing: 0, retrying: 0, deadLettered: 1 }, storage: { provider: "filesystem" }, workers: [{ workerId: "worker-1", status: "Connected", lastHeartbeat: null, capabilities: [{ contractId: "platform.jobs.hash", contractVersion: "1.0" }], runningExecutions: 1 }], metrics: [{ name: "hispania.jobs.created", kind: "Counter", value: 3, count: 0 }] }));
  vi.stubGlobal("fetch", fetch); render(<App variant="classic" />);
  expect(await screen.findByRole("heading", { name: "Health: Degraded" })).toBeInTheDocument();
  expect(screen.getByText("worker-1")).toBeInTheDocument(); expect(screen.getByText("hispania.jobs.created")).toBeInTheDocument();
  expect(screen.getByText(/Los contadores se reinician/)).toBeInTheDocument();
  const calls = fetch.mock.calls as unknown as [RequestInfo | URL, RequestInit][];
  const adminCall = calls.find(call => call[0] === "/api/platform/observability");
  expect(adminCall).toBeDefined();
  expect(new Headers(adminCall?.[1].headers).get("Authorization")).toBe("Session opaque-session");
  expect(screen.getByText(/Liveness: Healthy; Readiness: Unhealthy/)).toBeInTheDocument();
});
