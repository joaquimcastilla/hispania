import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";

function catalogResponse(body: unknown, ok = true) {
  return Promise.resolve({ ok, json: async () => body } as Response);
}

describe("App", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("renders a loading state", () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {})));

    render(<App />);

    expect(screen.getByRole("status")).toHaveTextContent("Cargando películas…");
  });

  it("renders an empty catalog", async () => {
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([])));

    render(<App />);

    expect(await screen.findByText("El catálogo está vacío.")).toBeInTheDocument();
  });

  it("renders catalog media and selects a movie with its direct play URL", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([{ id: "media-id", displayName: "Big Buck Bunny" }])));

    render(<App />);

    const selection = await screen.findByRole("button", { name: "Big Buck Bunny" });
    await user.click(selection);

    expect(selection).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("heading", { name: "Big Buck Bunny", level: 2 })).toBeInTheDocument();
    expect(document.querySelector("video")).toHaveAttribute("src", "/api/v1/media/media-id/stream");
  });

  it("renders an error when the catalog request fails", async () => {
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse(null, false)));

    render(<App />);

    expect(await screen.findByRole("alert")).toHaveTextContent("No se ha podido cargar el catálogo.");
  });
});
