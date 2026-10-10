import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AdminPanel } from "./AdminPanel";

afterEach(() => vi.unstubAllGlobals());

describe("AdminPanel metadata scan report", () => {
  it("lists movies left without metadata and explains why", async () => {
    const response = {
      library: { discovered: 1, imported: 1, skipped: 0, failed: 0 },
      metadata: {
        processedItems: 1,
        conflicts: 0,
        failures: 0,
        itemsWithoutMetadata: [{ displayName: "American Beauty (1999)", reasonCode: "no-match", detail: "No exact match." }]
      }
    };
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => response } as Response)));
    const updated = vi.fn();
    render(<AdminPanel movieCount={1} onMetadataUpdated={updated} />);
    fireEvent.click(screen.getByRole("button", { name: "Volver a escanear toda la biblioteca" }));
    expect(fetch).toHaveBeenCalledWith("/api/v1/admin/metadata/scan", expect.objectContaining({ method: "POST", headers: expect.any(Headers) }));
    expect(await screen.findByRole("region", { name: "Películas sin metadata" })).toBeInTheDocument();
    expect(screen.getByText("American Beauty (1999)")).toBeInTheDocument();
    expect(screen.getByText("Sin coincidencia en TMDB")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("con metadata: 0; sin metadata: 1");
    expect(updated).toHaveBeenCalledOnce();
  });

  it("does not report a successful zero-error scan when the backend omits per-movie results", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => ({ library: { discovered: 1, imported: 1, skipped: 0, failed: 0 }, metadata: { processedItems: 1, conflicts: 0, failures: 0 } }) } as Response)));
    render(<AdminPanel movieCount={1} onMetadataUpdated={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "Volver a escanear toda la biblioteca" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("El backend activo no informa qué películas quedaron sin metadata");
  });

  it("uses the incremental endpoint for new or unidentified movies", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => ({
      library: { discovered: 3, imported: 1, skipped: 2, failed: 0 },
      metadata: { processedItems: 1, conflicts: 0, failures: 0, itemsWithoutMetadata: [] }
    }) } as Response)));
    render(<AdminPanel movieCount={3} onMetadataUpdated={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "Buscar metadata de películas nuevas" }));
    expect(await screen.findByRole("status")).toHaveTextContent("películas nuevas o sin identificación TMDB");
    expect(fetch).toHaveBeenCalledWith("/api/v1/admin/metadata/scan-new", expect.objectContaining({ method: "POST", headers: expect.any(Headers) }));
  });
});
