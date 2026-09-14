import { useEffect, useState } from "react";

type ArtworkLocator = { scheme: string; value: string };
type MediaListItem = { id: string; displayName: string; effective?: { title?: string | null; releaseDate?: string | null; poster?: ArtworkLocator | null } };
type MetadataField = { value?: string | null };
type MetadataDetail = { title: MetadataField; originalTitle: MetadataField; description: MetadataField; releaseDate: MetadataField; artwork: { poster: { locatorScheme?: string | null }; backdrop: { locatorScheme?: string | null } } };

const catalogUrl = "/api/v1/media";
const presentationTitle = (item: MediaListItem) => item.effective?.title || item.displayName;
const releaseYear = (date?: string | null) => date ? date.slice(0, 4) : null;

export function streamUrl(mediaId: string): string { return `/api/v1/media/${encodeURIComponent(mediaId)}/stream`; }

export function App() {
  const [media, setMedia] = useState<MediaListItem[]>([]);
  const [selectedMedia, setSelectedMedia] = useState<MediaListItem | null>(null);
  const [detail, setDetail] = useState<MetadataDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void fetch(catalogUrl, { signal: controller.signal }).then(async response => response.ok ? response.json() : Promise.reject(new Error("Catalog request failed.")))
      .then((catalog: unknown) => setMedia(Array.isArray(catalog) ? catalog as MediaListItem[] : []))
      .catch(() => { if (!controller.signal.aborted) setError("No se ha podido cargar el catálogo."); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!selectedMedia) return;
    const controller = new AbortController(); setDetail(null); setDetailLoading(true);
    void fetch(`/api/v1/media/${encodeURIComponent(selectedMedia.id)}/metadata`, { signal: controller.signal }).then(async response => response.ok ? response.json() : Promise.reject(new Error("Metadata request failed.")))
      .then((value: MetadataDetail) => setDetail(value)).catch(() => { if (!controller.signal.aborted) setDetail(null); })
      .finally(() => { if (!controller.signal.aborted) setDetailLoading(false); });
    return () => controller.abort();
  }, [selectedMedia]);

  return <main className="app-shell">
    <header><h1>HISPANIA</h1></header>
    <section aria-labelledby="movies-heading"><h2 id="movies-heading">Películas</h2>
      {loading && <p role="status">Cargando películas…</p>}{error && <p role="alert">{error}</p>}
      {!loading && !error && media.length === 0 && <p>El catálogo está vacío.</p>}
      {!loading && !error && media.length > 0 && <ul className="movie-grid">{media.map(item => <li key={item.id}>
        <button type="button" className="movie-card" aria-pressed={selectedMedia?.id === item.id} onClick={() => setSelectedMedia(item)}>
          <span className="artwork-placeholder" aria-hidden="true">▣</span>
          <span className="movie-title">{presentationTitle(item)}</span>{releaseYear(item.effective?.releaseDate) && <span className="movie-year">{releaseYear(item.effective?.releaseDate)}</span>}
        </button></li>)}</ul>}
    </section>
    {selectedMedia && <section className="player" aria-labelledby="player-heading"><h2 id="player-heading">{detail?.title?.value || presentationTitle(selectedMedia)}</h2>
      {detailLoading && <p role="status">Cargando detalles…</p>}{detail?.originalTitle?.value && <p className="original-title">{detail.originalTitle.value}</p>}{detail?.releaseDate?.value && <p>{detail.releaseDate.value}</p>}{detail?.description?.value && <p>{detail.description.value}</p>}
      {!detailLoading && !detail && <p>Metadata no disponible.</p>}<div className="detail-artwork artwork-placeholder" aria-hidden="true">▣</div>
      <video controls preload="metadata" src={streamUrl(selectedMedia.id)}>Your browser does not support video playback.</video>
    </section>}
  </main>;
}
