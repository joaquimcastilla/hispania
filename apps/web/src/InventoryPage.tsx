import { useEffect, useState } from "react";
import "./InventoryPage.css";

type WrappedId = { value: string };
type InventoryItem = { id: WrappedId; sku: string; name: string; assets: { id: WrappedId }[] };
type Warehouse = { id: WrappedId; code: string; name: string };
type Location = { id: WrappedId; warehouseId: WrappedId; code: string; name: string };
type Stock = { itemId: WrappedId; locationId: WrappedId; quantity: number };
type Asset = { id: WrappedId; type: string; mimeType: string; size: number; metadata: Record<string, string> };
type PlaybackCapability = { available: boolean; streamUrl: string; mimeType: string };

async function get<T>(path: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json() as Promise<T>;
}

export function InventoryPage() {
  const [items, setItems] = useState<InventoryItem[]>([]);
  const [warehouses, setWarehouses] = useState<Warehouse[]>([]);
  const [locations, setLocations] = useState<Location[]>([]);
  const [stock, setStock] = useState<Stock[]>([]);
  const [assets, setAssets] = useState<Record<string, Asset | null>>({});
  const [playback, setPlayback] = useState<Record<string, PlaybackCapability | null>>({});
  const [error, setError] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    void Promise.all([
      get<InventoryItem[]>("/api/inventory/items", controller.signal),
      get<Warehouse[]>("/api/inventory/warehouses", controller.signal),
      get<Location[]>("/api/inventory/locations", controller.signal),
      get<Stock[]>("/api/inventory/stock", controller.signal),
    ]).then(async ([nextItems, nextWarehouses, nextLocations, nextStock]) => {
      setItems(nextItems); setWarehouses(nextWarehouses); setLocations(nextLocations); setStock(nextStock);
      const ids = [...new Set(nextItems.flatMap(item => item.assets.map(asset => asset.id.value)))];
      const resolved = await Promise.all(ids.map(async id => {
        try { return [id, await get<Asset>(`/api/inventory/assets/${encodeURIComponent(id)}`, controller.signal)] as const; }
        catch { return [id, null] as const; }
      }));
      if (!controller.signal.aborted) {
        setAssets(Object.fromEntries(resolved));
        const videos = resolved.filter((entry): entry is readonly [string, Asset] => entry[1]?.type === "Video");
        const capabilities = await Promise.all(videos.map(async ([id]) => {
          try { return [id, await get<PlaybackCapability>(`/api/media/assets/${encodeURIComponent(id)}/playback`, controller.signal)] as const; }
          catch { return [id, null] as const; }
        }));
        if (!controller.signal.aborted) setPlayback(Object.fromEntries(capabilities));
      }
    }).catch(() => { if (!controller.signal.aborted) setError(true); });
    return () => controller.abort();
  }, []);

  return <section className="inventory-page" aria-labelledby="inventory-heading">
    <span className="eyebrow">ERP · Inventario</span><h1 id="inventory-heading">Inventario</h1>
    <p className="inventory-intro">Artículos, existencias por ubicación y documentos relacionados.</p>
    {error ? <p className="inventory-state" role="alert">No se pudo cargar Inventory. Comprueba la conexión con HISPANIA.</p> : items.length === 0 ? <p className="inventory-state" role="status">No hay artículos en inventario.</p> : <div className="inventory-list">
      {items.map(item => <article className="inventory-card" key={item.id.value}>
        <header><div><span className="inventory-sku">{item.sku}</span><h2>{item.name}</h2></div><strong className="inventory-total">{stock.filter(level => level.itemId.value === item.id.value).reduce((total, level) => total + level.quantity, 0)} unidades</strong></header>
        <div className="inventory-locations"><h3>Existencias</h3>{stock.filter(level => level.itemId.value === item.id.value).map(level => {
          const location = locations.find(candidate => candidate.id.value === level.locationId.value);
          const warehouse = warehouses.find(candidate => candidate.id.value === location?.warehouseId.value);
          return <div className="inventory-stock-row" key={level.locationId.value}><span>{warehouse?.name ?? "Almacén"} · {location?.name ?? "Ubicación"}</span><strong>{level.quantity}</strong></div>;
        })}</div>
        {item.assets.length > 0 && <div className="inventory-assets"><h3>Assets asociados</h3><ul>{item.assets.map(reference => {
          const asset = assets[reference.id.value];
          const contentUrl = `/api/assets/${encodeURIComponent(reference.id.value)}/content`;
          return <li key={reference.id.value}><span className="asset-kind">{asset?.type ?? "Asset"}</span><span>{asset?.metadata.label ?? "Referencia asociada"}</span><small>{asset ? `${asset.mimeType} · ${asset.size} bytes` : "Metadata no disponible"}</small>
            {asset?.type === "Image" && <img className="inventory-asset-image" src={contentUrl} alt={asset.metadata.label ?? item.name} />}
            {asset?.type === "Document" && <a href={contentUrl} target="_blank" rel="noreferrer">Abrir ficha técnica</a>}
            {asset?.type === "Video" && playback[reference.id.value]?.available && <video className="inventory-asset-video" controls preload="metadata" src={playback[reference.id.value]?.streamUrl}><track kind="captions" /></video>}
          </li>;
        })}</ul></div>}
      </article>)}
    </div>}
  </section>;
}
