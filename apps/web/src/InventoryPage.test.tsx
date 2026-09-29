import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { InventoryPage } from "./InventoryPage";

describe("InventoryPage", () => {
  afterEach(() => vi.unstubAllGlobals());
  it("composes inventory records with asset metadata through public APIs", async () => {
    const itemId = "00000000-0000-0000-0000-000000000001";
    const locationId = "00000000-0000-0000-0000-000000000002";
    const warehouseId = "00000000-0000-0000-0000-000000000003";
    const assetId = "00000000-0000-0000-0000-000000000004";
    const responses: Record<string, unknown> = {
      "/api/inventory/items": [{ id: { value: itemId }, sku: "BP48", name: "Battery Pack 48V", assets: [{ id: { value: assetId } }] }],
      "/api/inventory/warehouses": [{ id: { value: warehouseId }, code: "MAD", name: "Madrid Warehouse" }],
      "/api/inventory/locations": [{ id: { value: locationId }, warehouseId: { value: warehouseId }, code: "A-01", name: "Shelf A-01" }],
      "/api/inventory/stock": [{ itemId: { value: itemId }, locationId: { value: locationId }, quantity: 12 }],
      [`/api/inventory/assets/${assetId}`]: { id: { value: assetId }, type: "Document", mimeType: "application/pdf", size: 34, metadata: { label: "Datasheet PDF" } },
    };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const key = String(input);
      return { ok: key in responses, status: key in responses ? 200 : 404, json: async () => responses[key] } as Response;
    }));
    render(<InventoryPage />);
    expect(await screen.findByRole("heading", { name: "Battery Pack 48V" })).toBeInTheDocument();
    expect(screen.getByText("12 unidades")).toBeInTheDocument();
    expect(screen.getByText("Madrid Warehouse · Shelf A-01")).toBeInTheDocument();
    expect(await screen.findByText("Datasheet PDF")).toBeInTheDocument();
    expect(screen.getByText("application/pdf · 34 bytes")).toBeInTheDocument();
  });
});
