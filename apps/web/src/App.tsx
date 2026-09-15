import { useEffect, useState } from "react";

type MediaListItem = { id: string; displayName: string; effective?: { title?: string | null; releaseDate?: string | null; posterUrl?: string | null } };
type MetadataField = { value?: string | null };
type MetadataArtwork = { deliveryUrl?: string | null };
type MetadataDetail = { title: MetadataField; originalTitle: MetadataField; description: MetadataField; releaseDate: MetadataField; artwork: { poster: MetadataArtwork; backdrop: MetadataArtwork } };
type LibrarySection = "movies" | "series" | "collections" | "music";

const catalogUrl = "/api/v1/media";
const navigation: ReadonlyArray<{ id: LibrarySection; label: string }> = [{ id: "movies", label: "Películas" }, { id: "series", label: "Series" }, { id: "collections", label: "Colecciones" }, { id: "music", label: "Música" }];
const presentationTitle = (item: MediaListItem) => item.effective?.title || item.displayName;
const releaseYear = (date?: string | null) => date ? date.slice(0, 4) : null;
export function streamUrl(mediaId: string): string { return `/api/v1/media/${encodeURIComponent(mediaId)}/stream`; }

function Artwork({ src, alt, className, placeholderTestId }: { src?: string | null; alt: string; className: string; placeholderTestId: string }) {
  const [failed, setFailed] = useState(false);
  if (src && !failed) return <img className={className} src={src} alt={alt} loading="lazy" onError={() => setFailed(true)} />;
  return <span className={`${className} artwork-placeholder`} data-testid={placeholderTestId} aria-hidden="true">✦</span>;
}

function UnavailableSection({ title }: { title: string }) {
  return <section className="unavailable-section" aria-labelledby={`${title}-heading`}>
    <span className="eyebrow">HISPANIA</span><h1 id={`${title}-heading`}>{title}</h1>
    <div className="unavailable-card"><span className="unavailable-symbol" aria-hidden="true">◇</span><h2>Aún no disponible</h2><p>Esta sección está preparada para cuando el catálogo tenga soporte para {title.toLowerCase()}.</p></div>
  </section>;
}

export function App() {
  const [section, setSection] = useState<LibrarySection>("movies");
  const [media, setMedia] = useState<MediaListItem[]>([]);
  const [selectedMedia, setSelectedMedia] = useState<MediaListItem | null>(null);
  const [detail, setDetail] = useState<MetadataDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void fetch(catalogUrl, { signal: controller.signal }).then(async response => response.ok ? response.json() : Promise.reject(new Error())).then((catalog: unknown) => setMedia(Array.isArray(catalog) ? catalog as MediaListItem[] : [])).catch(() => { if (!controller.signal.aborted) setError("No se ha podido cargar el catálogo."); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!selectedMedia) return;
    const controller = new AbortController();
    setDetail(null); setDetailError(false); setDetailLoading(true); setPlaying(false);
    void fetch(`/api/v1/media/${encodeURIComponent(selectedMedia.id)}/metadata`, { signal: controller.signal }).then(async response => response.ok ? response.json() : Promise.reject(new Error())).then((value: MetadataDetail) => setDetail(value)).catch(() => { if (!controller.signal.aborted) setDetailError(true); }).finally(() => { if (!controller.signal.aborted) setDetailLoading(false); });
    return () => controller.abort();
  }, [selectedMedia]);

  const selectSection = (nextSection: LibrarySection) => { setSection(nextSection); if (nextSection !== "movies") setPlaying(false); };
  const detailTitle = detail?.title?.value || (selectedMedia ? presentationTitle(selectedMedia) : "");

  return <main className="app-shell">
    <header className="topbar"><button className="brand" type="button" onClick={() => selectSection("movies")} aria-label="HISPANIA, ir a Películas"><span className="brand-mark" aria-hidden="true">H</span><span>HISPANIA</span></button><nav className="main-navigation" aria-label="Navegación principal">{navigation.map(item => <button key={item.id} type="button" className="nav-item" aria-current={section === item.id ? "page" : undefined} onClick={() => selectSection(item.id)}>{item.label}</button>)}</nav></header>
    {section === "movies" && <section className="movie-library" aria-labelledby="movies-heading"><div className="section-heading"><div><span className="eyebrow">Tu biblioteca</span><h1 id="movies-heading">Películas</h1><p>Historias listas para volver a descubrir.</p></div><span className="catalog-count">{media.length} títulos</span></div>{loading && <div className="state-panel" role="status"><span className="state-symbol" aria-hidden="true">◌</span>Cargando películas…</div>}{error && <div className="state-panel state-error" role="alert">{error}</div>}{!loading && !error && media.length === 0 && <div className="state-panel"><span className="state-symbol" aria-hidden="true">◇</span><strong>El catálogo está vacío.</strong><span>Aquí aparecerán las películas cuando estén disponibles en tu biblioteca.</span></div>}{!loading && !error && media.length > 0 && <ul className="movie-grid">{media.map(item => <li key={item.id}><button type="button" className="movie-card" aria-pressed={selectedMedia?.id === item.id} onClick={() => setSelectedMedia(item)}><Artwork key={item.effective?.posterUrl} src={item.effective?.posterUrl} alt={presentationTitle(item)} className="card-artwork" placeholderTestId="grid-artwork-placeholder" /><span className="movie-card-copy"><span className="movie-title">{presentationTitle(item)}</span>{releaseYear(item.effective?.releaseDate) && <span className="movie-year">{releaseYear(item.effective?.releaseDate)}</span>}</span></button></li>)}</ul>}</section>}
    {section === "series" && <UnavailableSection title="Series" />}{section === "collections" && <UnavailableSection title="Colecciones" />}{section === "music" && <UnavailableSection title="Música" />}
    {section === "movies" && selectedMedia && <section className="movie-detail" aria-labelledby="detail-heading"><div className="detail-hero"><Artwork key={detail?.artwork?.backdrop?.deliveryUrl} src={detail?.artwork?.backdrop?.deliveryUrl} alt="" className="detail-backdrop" placeholderTestId="backdrop-placeholder" /><div className="hero-shade" /></div><div className="detail-content"><Artwork key={detail?.artwork?.poster?.deliveryUrl} src={detail?.artwork?.poster?.deliveryUrl} alt={detailTitle} className="detail-artwork" placeholderTestId="detail-artwork-placeholder" /><div className="detail-copy"><span className="eyebrow">Ahora viendo</span><h2 id="detail-heading">{detailTitle}</h2>{detailLoading && <p role="status">Cargando detalles…</p>}{detailError && <p role="alert">Metadata no disponible.</p>}{detail?.originalTitle?.value && <p className="original-title">{detail.originalTitle.value}</p>}{detail?.releaseDate?.value && <p className="detail-date">{detail.releaseDate.value}</p>}{detail?.description?.value && <p className="detail-description">{detail.description.value}</p>}<div className="detail-actions"><button type="button" className="play-button" onClick={() => setPlaying(true)}><span aria-hidden="true">▶ </span>Play</button><button type="button" className="close-detail-button" onClick={() => setSelectedMedia(null)}>Cerrar detalle</button></div></div></div>{playing && <div className="player"><div className="player-heading"><h3>Reproduciendo {detailTitle}</h3><button type="button" onClick={() => setPlaying(false)}>Cerrar reproductor</button></div><video controls autoPlay preload="metadata" src={streamUrl(selectedMedia.id)}>Your browser does not support video playback.</video></div>}</section>}
    <footer className="tmdb-attribution">This product uses the TMDB API but is not endorsed or certified by TMDB.</footer>
  </main>;
}
