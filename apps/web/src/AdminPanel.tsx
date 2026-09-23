import { useState } from "react";
import "./AdminPanel.css";

type ScanResult = { library: { discovered: number; imported: number; skipped: number; failed: number }; metadata: { processedItems: number; conflicts: number; failures: number; itemsWithoutMetadata: { displayName: string; reasonCode: string; detail: string }[] } };
type MetadataIssue = ScanResult["metadata"]["itemsWithoutMetadata"][number];

const issueLabels: Record<string, string> = {
  "no-match": "Sin coincidencia en TMDB",
  ambiguous: "Hay varias coincidencias posibles",
  "provider-error": "Error al consultar el proveedor",
  "metadata-fetch-failed": "No se pudo descargar la metadata",
  "identifier-conflict": "Conflicto de identificación",
  "identifier-conflict-existing": "Conflicto de identificación pendiente de revisión"
};

const navigation: ReadonlyArray<{ group: string; items: ReadonlyArray<{ label: string; icon: string }> }> = [
  { group: "Servidor", items: [{ label: "Panel de control", icon: "▦" }, { label: "General", icon: "⚙" }, { label: "Personalización de apariencia", icon: "◉" }, { label: "Usuarios", icon: "♙" }, { label: "Bibliotecas", icon: "▣" }, { label: "Reproducción", icon: "▶" }] },
  { group: "Dispositivos", items: [{ label: "Dispositivos", icon: "▣" }, { label: "Actividad", icon: "▤" }] },
  { group: "Televisión en vivo", items: [{ label: "Televisión en vivo", icon: "▣" }, { label: "Grabador Digital (DVR)", icon: "▤" }] },
  { group: "Complementos", items: [{ label: "Complementos", icon: "✣" }] },
  { group: "Avanzado", items: [{ label: "Redes", icon: "⌘" }, { label: "Claves API", icon: "⚿" }, { label: "Copias de seguridad", icon: "☁" }, { label: "Registros", icon: "▤" }, { label: "Tareas programadas", icon: "◷" }] }
];

export function AdminPanel({ movieCount, onMetadataUpdated }: { movieCount: number; onMetadataUpdated: () => void }) {
  const [scanning, setScanning] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [resultIssues, setResultIssues] = useState<MetadataIssue[]>([]);

  const scanMetadata = async (mode: "all" | "new") => {
    setScanning(true);
    setMessage(null);
    setError(null);
    setResultIssues([]);
    try {
      const response = await fetch(mode === "all" ? "/api/v1/admin/metadata/scan" : "/api/v1/admin/metadata/scan-new", { method: "POST" });
      if (response.status === 409) throw new Error("Ya hay un escaneo de metadata en curso.");
      if (response.status === 503) throw new Error("TMDB no está habilitado. Introduce el token TMDB en HISPANIA Dev Launcher y pulsa Reiniciar; después vuelve a escanear.");
      if (!response.ok) throw new Error("No se pudo completar el escaneo de metadata.");
      const result = await response.json() as ScanResult;
      if (!Array.isArray(result.metadata.itemsWithoutMetadata)) throw new Error("El backend activo no informa qué películas quedaron sin metadata. Pulsa Reiniciar en HISPANIA Dev Launcher y vuelve a escanear.");
      setResultIssues(result.metadata.itemsWithoutMetadata);
      const metadataWithResult = Math.max(0, result.metadata.processedItems - result.metadata.itemsWithoutMetadata.length);
      const scanKind = mode === "all" ? "revisión completa" : "películas nuevas o sin identificación TMDB";
      setMessage(`Escaneo completado (${scanKind}). Carpetas y archivos de vídeo descubiertos: ${result.library.discovered}; importados o actualizados: ${result.library.imported}; omitidos: ${result.library.skipped}; errores de biblioteca: ${result.library.failed}. Metadata revisada: ${result.metadata.processedItems}; con metadata: ${metadataWithResult}; sin metadata: ${result.metadata.itemsWithoutMetadata.length}; conflictos: ${result.metadata.conflicts}; fallos técnicos: ${result.metadata.failures}.`);
      onMetadataUpdated();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "No se pudo completar el escaneo de metadata.");
    } finally {
      setScanning(false);
    }
  };

  return <section className="admin-layout" aria-label="Administración">
    <aside className="admin-sidebar" aria-label="Opciones del servidor">
      {navigation.map(group => <div className="admin-nav-group" key={group.group}>
        <span className="admin-nav-heading">{group.group}</span>
        {group.items.map(item => item.label === "Panel de control"
          ? <button className="admin-nav-item selected" type="button" key={item.label} aria-current="page"><span aria-hidden="true">{item.icon}</span><span>{item.label}</span></button>
          : <button className="admin-nav-item" type="button" key={item.label} disabled aria-disabled="true"><span aria-hidden="true">{item.icon}</span><span>{item.label}</span></button>)}
      </div>)}
    </aside>

    <main className="admin-main">
      <div className="admin-title"><span className="eyebrow">Servidor</span><h1>Panel de control</h1></div>
      <div className="admin-dashboard-grid">
        <section className="admin-primary-column">
          <div className="admin-card server-summary">
            <h2>Servidor</h2>
            <dl><dt>Nombre del servidor</dt><dd>HISPANIA</dd><dt>Proveedor de metadata</dt><dd>TMDB</dd><dt>Biblioteca</dt><dd>Películas</dd></dl>
          </div>
          <div className="admin-actions">
            <div className="metadata-scan-buttons">
              <button type="button" className="metadata-scan-button" onClick={() => void scanMetadata("all")} disabled={scanning}>
                <span aria-hidden="true">↻</span>{scanning ? "Escaneando…" : "Volver a escanear toda la biblioteca"}
              </button>
              <button type="button" className="metadata-scan-button metadata-scan-button-secondary" onClick={() => void scanMetadata("new")} disabled={scanning}>
                <span aria-hidden="true">＋</span>{scanning ? "Escaneando…" : "Buscar metadata de películas nuevas"}
              </button>
            </div>
            {message && <p className="admin-scan-message" role="status">{message}</p>}
            {error && <p className="admin-scan-error" role="alert">{error}</p>}
            {message && resultIssues.length > 0 && <section className="admin-scan-issues" aria-label="Películas sin metadata"><h3>Películas sin metadata ({resultIssues.length})</h3><ul>{resultIssues.map((issue, index) => <li key={`${issue.displayName}-${index}`}><strong>{issue.displayName}</strong><span>{issueLabels[issue.reasonCode] ?? issue.reasonCode}</span><small>{issue.detail}</small></li>)}</ul></section>}
            <p className="admin-scan-help">Vuelve a revisar las carpetas de películas, detecta vídeos nuevos y consulta TMDB para actualizar metadata e imágenes.</p>
          </div>
          <div className="admin-stats">
            <article className="admin-card stat-card"><span>Películas</span><strong>{movieCount}</strong></article>
            <article className="admin-card stat-card"><span>Proveedor activo</span><strong>TMDB</strong></article>
            <article className="admin-card stat-card"><span>Acción</span><strong>Metadata</strong></article>
          </div>
        </section>

        <section className="admin-card admin-activity"><h2>Actividad</h2><div className="admin-empty-activity"><span aria-hidden="true">◉</span><p>{message ?? "El resultado del escaneo aparecerá aquí."}</p></div></section>
        <section className="admin-card admin-paths"><h2>Rutas</h2>{["Biblioteca de películas", "Metadata", "Imágenes"].map((item, index) => <div className="admin-path-row" key={item}><span aria-hidden="true">▤</span><div><small>{item}</small><strong>{index === 0 ? "Configurada en Dev Launcher" : index === 1 ? "TMDB" : "Entregadas por metadata"}</strong><div className="admin-path-meter"><span /></div></div></div>)}</section>
      </div>
    </main>
  </section>;
}
