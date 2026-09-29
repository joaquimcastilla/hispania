import { useEffect, useRef, useState } from "react";
import { MetadataEditor } from "./MetadataEditor";
import { ContentCarousel, ContentCarouselItem } from "./ContentCarousel";
import { AdminPanel } from "./AdminPanel";
import { InventoryPage } from "./InventoryPage";
import { moduleNavigation, routeForPath, shellRoutes } from "./webModules";
import { metadataClient, MetadataEditModel, PersonMetadata } from "./metadataClient";
import "./MovieExperience.css";

type MediaListItem = { id: string; displayName: string; effective?: { title?: string | null; releaseDate?: string | null; posterUrl?: string | null }; technical?: { durationSeconds?: number | null; container?: string | null; video?: { codec: string; width: number; height: number; bitDepth?: number | null } | null; audioTracks?: { representationId: string; streamIndex?: number | null; codec: string; channels: number; sampleRateHertz: number }[] } };
type MetadataField = { value?: string | number | null; provider?: string | null };
type MetadataArtwork = { deliveryUrl?: string | null };
type MetadataDetail = { title: MetadataField; originalTitle: MetadataField; description: MetadataField; releaseDate: MetadataField; artwork: { poster: MetadataArtwork; backdrop: MetadataArtwork } };
type LibrarySection = "home" | "movies" | "series" | "collections" | "music" | "dashboard" | "inventory";
type LocalPerson = NonNullable<MetadataEditModel["people"]>[number];

const catalogUrl = "/api/v1/media";
const navigation: ReadonlyArray<{ id: LibrarySection; label: string }> = moduleNavigation.map(route => ({ id: route.section, label: route.label }));
const routeForSection = (section: LibrarySection) => shellRoutes.find(route => route.section === section);
const presentationTitle = (item: MediaListItem) => item.effective?.title || item.displayName;
const releaseYear = (date?: string | null) => date ? date.slice(0, 4) : null;
export function streamUrl(mediaId: string): string { return `/api/v1/media/${encodeURIComponent(mediaId)}/stream`; }
const metadataValue = (model: MetadataEditModel | undefined, code: string) => model?.fields?.find(field => field.fieldCode === code)?.effective;
const personArtworkUrl = (person: LocalPerson) => person.providerCode === "tmdb" && person.profileArtworkLocator?.startsWith("/") ? `https://image.tmdb.org/t/p/w185${person.profileArtworkLocator}` : null;
const formatRuntime = (seconds?: number | null) => seconds && seconds > 0 ? `${Math.floor(seconds / 3600)} h ${Math.floor((seconds % 3600) / 60)} min` : null;
const formatChannels = (channels: number) => channels === 6 ? "5.1" : channels === 8 ? "7.1" : channels === 2 ? "Estéreo" : `${channels} canales`;

function Home({ onNavigate, movies, onOpenMovie }: { onNavigate: (section: LibrarySection) => void; movies: MediaListItem[]; onOpenMovie: (item: MediaListItem) => void }) {
  const libraries: ContentCarouselItem[] = [{ id: "movies", label: "Películas", className: "content-card-movies", onActivate: () => onNavigate("movies") }, { id: "series", label: "Series", description: "Próximamente", className: "content-card-series", onActivate: () => onNavigate("series") }, { id: "collections", label: "Colecciones", description: "Próximamente", className: "content-card-collections", onActivate: () => onNavigate("collections") }, { id: "music", label: "Música", description: "Próximamente", className: "content-card-music", onActivate: () => onNavigate("music") }];
  const recentMovies: ContentCarouselItem[] = movies.map(item => ({ id: item.id, label: presentationTitle(item), description: releaseYear(item.effective?.releaseDate) ?? undefined, artworkUrl: item.effective?.posterUrl, onActivate: () => onOpenMovie(item) }));
  return <section className="home-page" aria-labelledby="home-heading"><div className="home-heading"><span className="eyebrow">HISPANIA</span><h1 id="home-heading">Inicio</h1></div><ContentCarousel title="Mis contenidos" items={libraries} ariaLabel="Bibliotecas disponibles" variant="library" /><ContentCarousel title="Seguir viendo" items={[]} variant="landscape" /><ContentCarousel title="Reciente en Películas" items={recentMovies} variant="poster" ariaLabel="Películas del catálogo" /></section>;
}

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

function MovieMetadataPresentation({ model, people, credits, technical, year, factsOnly = false, onOpenPerson, now }: { model?: MetadataEditModel; people: NonNullable<MetadataEditModel["people"]>; credits: NonNullable<MetadataEditModel["credits"]>; technical?: MediaListItem["technical"]; year?: string | null; factsOnly?: boolean; onOpenPerson?: (person: LocalPerson) => void; now?: Date }) {
  const personName = (id: string) => people.find(person => person.internalPersonId === id)?.name ?? id;
  const creditsOf = (types: string[]) => <span className="credit-links">{[...new Map(credits.filter(credit => types.includes(credit.creditType.toLowerCase())).map(credit => [credit.personId, people.find(person => person.internalPersonId === credit.personId)])).entries()].map(([id, person], index) => <span key={id}>{index > 0 && ", "}{person?.internalPersonId && onOpenPerson ? <button type="button" className="person-credit-link" onClick={() => onOpenPerson(person)}>{person.name}</button> : person?.name ?? id}</span>)}</span>;
  const studios = model?.collections?.find(collection => collection.collectionCode === "studios")?.effective ?? [];
  const genres = model?.collections?.find(collection => collection.collectionCode === "genres")?.effective ?? [];
  const runtimeMinutes = metadataValue(model, "runtime-minutes");
  const runtime = typeof runtimeMinutes === "number" ? `${Math.floor(runtimeMinutes / 60)} h ${runtimeMinutes % 60} min` : formatRuntime(technical?.durationSeconds);
  const durationSeconds = typeof runtimeMinutes === "number" ? runtimeMinutes * 60 : technical?.durationSeconds;
  const finishAt = factsOnly && typeof durationSeconds === "number" && durationSeconds > 0 && now ? new Intl.DateTimeFormat("es-ES", { hour: "2-digit", minute: "2-digit" }).format(new Date(now.getTime() + durationSeconds * 1000)) : null;
  const rating = metadataValue(model, "community-rating");
  const certification = metadataValue(model, "content-rating");
  const tagline = metadataValue(model, "tagline");
  const imdb = metadataValue(model, "imdb-id");
  const video = technical?.video;
  const videoLabel = video ? `${video.width >= 3800 ? "4K" : video.height >= 1000 ? "1080p" : `${video.height}p`} ${video.codec.toUpperCase()}${video.bitDepth ? ` · ${video.bitDepth}-bit` : ""}` : technical?.container ?? "Formato no detectado";
  const rows = [["Dirigido por", creditsOf(["director"])], ["Guion", creditsOf(["writer", "screenplay"])], ["Estudios", studios.join(", ")], ["Géneros", genres.join(", ")]] as const;
  const facts = <section className="movie-facts" aria-label="Datos de la película">{year && <span>{year}</span>}{runtime && <span>{runtime}</span>}{finishAt && <span className="finish-time">Termina a las {finishAt}</span>}{typeof certification === "string" && certification && <span className="content-rating">{certification}</span>}{typeof rating === "number" && <span>★ {rating.toFixed(1)}</span>}{typeof imdb === "string" && imdb && <span className="metadata-badge">IMDb</span>}<span className="metadata-badge">TMDB</span></section>;
  if (factsOnly) return <>{facts}{typeof tagline === "string" && tagline && <p className="movie-tagline">{tagline}</p>}</>;
  return <div className="movie-information"><dl className="metadata-rows">{rows.map(([label, value]) => (typeof value === "string" ? value.length > 0 : credits.some(credit => (label === "Dirigido por" ? ["director"] : ["writer", "screenplay"]).includes(credit.creditType.toLowerCase()))) && <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl><div className="technical-list" aria-label="Formato y pistas"><div><span aria-hidden="true">▣</span><strong>{videoLabel}</strong></div><div><span aria-hidden="true">♫</span><select aria-label="Pista de audio" disabled value="default"><option value="default">{technical?.audioTracks?.[0] ? `${technical.audioTracks[0].codec.toUpperCase()} · ${formatChannels(technical.audioTracks[0].channels)} · Predeterminada` : "Audio no detectado"}</option>{(technical?.audioTracks ?? []).slice(1).map(track => <option key={track.representationId} value={track.representationId}>{track.codec.toUpperCase()} · {formatChannels(track.channels)}</option>)}</select></div><div><span aria-hidden="true">▤</span><select aria-label="Subtítulos" disabled value="none"><option value="none">Subtítulos externos no indexados</option></select></div></div></div>;
}

function CastPhoto({ person, name }: { person?: LocalPerson; name: string }) {
  const [failed, setFailed] = useState(false);
  const src = person ? personArtworkUrl(person) : null;
  return src && !failed ? <img className="cast-avatar cast-photo" src={src} alt={name} loading="lazy" onError={() => setFailed(true)} /> : <span className="cast-avatar">{name.slice(0, 1).toUpperCase()}</span>;
}

function PersonPage({ person, metadata, profileLoading, onBack, onOpenMovie, media }: { person: LocalPerson; metadata: PersonMetadata | null; profileLoading: boolean; onBack: () => void; onOpenMovie: (item: MediaListItem) => void; media: MediaListItem[] }) {
  const filmography = (metadata?.filmography ?? []).map(item => ({ id: item.mediaId, label: item.title || item.displayName, description: releaseYear(item.releaseDate) ?? undefined, artworkUrl: item.posterUrl, onActivate: () => onOpenMovie(media.find(movie => movie.id === item.mediaId) ?? { id: item.mediaId, displayName: item.title || item.displayName }) }));
  const profilePerson = metadata?.profileArtworkLocator ? { ...person, providerCode: metadata.providerCode ?? person.providerCode, profileArtworkLocator: metadata.profileArtworkLocator } : person;
  return <section className="person-page" aria-labelledby="person-heading">
    <button type="button" className="movie-back" onClick={onBack}>← Volver</button>
    <header className="person-profile">
      <CastPhoto person={profilePerson} name={metadata?.name ?? person.name} />
      <h1 id="person-heading">{metadata?.name ?? person.name}</h1>
      <div className="person-actions"><button type="button" aria-label={`Añadir ${person.name} a favoritos`} disabled>♡</button><button type="button" aria-label={`Más opciones de ${person.name}`} disabled>⋮</button></div>
    </header>
    <div className="person-biography">{profileLoading && <p role="status">Cargando perfil de TMDB…</p>}{metadata?.biography ? <p>{metadata.biography}</p> : !profileLoading && <p>TMDB no proporciona una biografía para este perfil.</p>}{metadata?.birthday && <p><strong>Nacido:</strong> {new Date(`${metadata.birthday}T12:00:00`).toLocaleDateString("es-ES")}</p>}{metadata?.deathday && <p><strong>Falleció:</strong> {new Date(`${metadata.deathday}T12:00:00`).toLocaleDateString("es-ES")}</p>}{metadata?.placeOfBirth && <p><strong>Lugar de nacimiento:</strong> {metadata.placeOfBirth}</p>}<span className="metadata-badge">TMDB</span></div>
    <ContentCarousel title="Películas" ariaLabel={`Películas de ${metadata?.name ?? person.name}`} variant="poster" items={filmography} />
  </section>;
}

export function App({ variant = "modern" }: { variant?: "modern" | "classic" }) {
  const [section, setSection] = useState<LibrarySection>(routeForPath(window.location.pathname)?.section ?? "movies");
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [media, setMedia] = useState<MediaListItem[]>([]);
  const initialMovieId = window.location.pathname.match(/^\/movies\/([^/]+)$/)?.[1] ? decodeURIComponent(window.location.pathname.match(/^\/movies\/([^/]+)$/)![1]) : null;
  const initialPersonId = window.location.pathname.match(/^\/people\/([^/]+)$/)?.[1] ? decodeURIComponent(window.location.pathname.match(/^\/people\/([^/]+)$/)![1]) : null;
  const [selectedMedia, setSelectedMedia] = useState<MediaListItem | null>(null);
  const [detail, setDetail] = useState<MetadataDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [person, setPerson] = useState<LocalPerson | null>(null);
  const [personMetadata, setPersonMetadata] = useState<PersonMetadata | null>(null);
  const [personProfileLoading, setPersonProfileLoading] = useState(false);
  const [personProfileError, setPersonProfileError] = useState(false);
  const [initialPersonLoadFailed, setInitialPersonLoadFailed] = useState(false);
  const [metadataByMovie, setMetadataByMovie] = useState<Record<string, MetadataEditModel>>({});
  const [creditsByMovie, setCreditsByMovie] = useState<Record<string, MetadataEditModel["credits"]>>({});
  const [peopleByMovie, setPeopleByMovie] = useState<Record<string, MetadataEditModel["people"]>>({});
  const [creditsLoading, setCreditsLoading] = useState(false);
  const videoRef = useRef<HTMLVideoElement>(null);
  const playerContainerRef = useRef<HTMLDivElement>(null);
  const [playbackError, setPlaybackError] = useState<string | null>(null);
  const [clockNow, setClockNow] = useState(() => new Date());
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);
  const [editingMediaId, setEditingMediaId] = useState<string | null>(() => window.location.pathname.match(/^\/media\/([^/]+)\/metadata\/edit$/)?.[1] ?? null);
  const openMovie = (item: MediaListItem) => { window.history.pushState({}, "", `/movies/${encodeURIComponent(item.id)}`); setPerson(null); setPersonMetadata(null); setSection("movies"); setSelectedMedia(item); document.documentElement.scrollTop = 0; document.body.scrollTop = 0; };
  const openEditor = (id: string) => { window.history.pushState({}, "", `/media/${encodeURIComponent(id)}/metadata/edit`); setEditingMediaId(id); };
  const closeEditor = () => { const path = selectedMedia ? `/movies/${encodeURIComponent(selectedMedia.id)}` : "/"; window.history.pushState({}, "", path); setEditingMediaId(null); };
  const openPerson = (candidate: LocalPerson) => { if (!candidate.internalPersonId) return; window.history.pushState({}, "", `/people/${encodeURIComponent(candidate.internalPersonId)}`); setPersonMetadata(null); setPerson(candidate); document.documentElement.scrollTop = 0; document.body.scrollTop = 0; };
  useEffect(() => { const timer = window.setInterval(() => setClockNow(new Date()), 30_000); return () => window.clearInterval(timer); }, []);
  useEffect(() => {
    if (!drawerOpen) return;
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === "Escape") setDrawerOpen(false); };
    document.addEventListener("keydown", closeOnEscape);
    return () => document.removeEventListener("keydown", closeOnEscape);
  }, [drawerOpen]);

  useEffect(() => {
    if (editingMediaId) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const delays = [500, 1000, 2000, 4000, 8000];
    const wait = (delay: number) => new Promise<void>(resolve => { timer = setTimeout(resolve, delay); });
    const load = async () => {
      setLoading(media.length === 0);
      for (let attempt = 0; ; attempt++) {
        try {
          const response = await fetch(catalogUrl, { signal: controller.signal });
          if (!response.ok) throw new Error();
          const catalog: unknown = await response.json();
          if (!controller.signal.aborted) { setMedia(Array.isArray(catalog) ? catalog as MediaListItem[] : []); setError(null); }
          return;
        } catch {
          if (controller.signal.aborted) return;
          setError(attempt < delays.length ? "El servidor está iniciándose o la conexión se ha interrumpido. Reintentando…" : "No se puede conectar con el servidor. Comprueba que HISPANIA está disponible e inténtalo de nuevo.");
          if (attempt >= delays.length) return;
          await wait(delays[attempt]);
        } finally { if (!controller.signal.aborted) setLoading(false); }
      }
    };
    void load();
    return () => { controller.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [reload, editingMediaId]);

  useEffect(() => {
    if (editingMediaId || !selectedMedia) return;
    const controller = new AbortController();
    setDetail(null); setDetailError(false); setDetailLoading(true); setPlaying(false);
    void fetch(`/api/v1/media/${encodeURIComponent(selectedMedia.id)}/metadata`, { signal: controller.signal }).then(async response => response.ok ? response.json() : Promise.reject(new Error())).then((value: MetadataDetail) => setDetail(value)).catch(() => { if (!controller.signal.aborted) setDetailError(true); }).finally(() => { if (!controller.signal.aborted) setDetailLoading(false); });
    return () => controller.abort();
  }, [selectedMedia, reload, editingMediaId]);

  useEffect(() => {
    const onPopState = () => {
      const editId = window.location.pathname.match(/^\/media\/([^/]+)\/metadata\/edit$/)?.[1] ?? null;
      const movieId = window.location.pathname.match(/^\/movies\/([^/]+)$/)?.[1];
      const personId = window.location.pathname.match(/^\/people\/([^/]+)$/)?.[1];
      if (window.location.pathname === "/erp/inventory") { setSection("inventory"); setSelectedMedia(null); setPerson(null); return; }
      setEditingMediaId(editId);
      if (movieId) { const id = decodeURIComponent(movieId); setSelectedMedia(media.find(item => item.id === id) ?? null); setSection("movies"); setPerson(null); }
      else if (personId) { const id = decodeURIComponent(personId); const found = Object.values(peopleByMovie).flat().find(p => p.internalPersonId === id); if (found) { setPerson(found); setSection("movies"); } }
      else if (!editId) { setSelectedMedia(null); setPerson(null); }
    };
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, [media, peopleByMovie]);

  useEffect(() => {
    if (!initialMovieId || selectedMedia || media.length === 0 || editingMediaId) return;
    const found = media.find(item => item.id === initialMovieId);
    if (found) { setSelectedMedia(found); setSection("movies"); }
  }, [initialMovieId, media, selectedMedia, editingMediaId]);

  useEffect(() => {
    if (!initialPersonId || person || editingMediaId) return;
    let cancelled = false;
    setInitialPersonLoadFailed(false);
    void metadataClient.getPerson(initialPersonId).then(value => {
      if (cancelled) return;
      setPersonMetadata(value);
      setPerson({ providerId: value.providerId, providerCode: value.providerCode, name: value.name, profileArtworkLocator: value.profileArtworkLocator, internalPersonId: value.personId });
      setSection("movies");
    }).catch(() => { if (!cancelled) setInitialPersonLoadFailed(true); });
    return () => { cancelled = true; };
  }, [initialPersonId, person, editingMediaId]);

  useEffect(() => {
    if (!selectedMedia || creditsByMovie[selectedMedia.id]) return;
    let cancelled = false;
    setCreditsLoading(true);
    void metadataClient.get(selectedMedia.id).then(value => { if (!cancelled) { setMetadataByMovie(current => ({ ...current, [selectedMedia.id]: value })); setCreditsByMovie(current => ({ ...current, [selectedMedia.id]: value.credits ?? [] })); setPeopleByMovie(current => ({ ...current, [selectedMedia.id]: value.people ?? [] })); } }).catch(() => { if (!cancelled) { setCreditsByMovie(current => ({ ...current, [selectedMedia.id]: [] })); setPeopleByMovie(current => ({ ...current, [selectedMedia.id]: [] })); } }).finally(() => { if (!cancelled) setCreditsLoading(false); });
    return () => { cancelled = true; };
  }, [selectedMedia, creditsByMovie]);

  useEffect(() => {
    if (!person?.internalPersonId) { setPersonMetadata(null); return; }
    if (personMetadata?.personId === person.internalPersonId) return;
    let cancelled = false;
    setPersonProfileLoading(true);
    setPersonProfileError(false);
    void metadataClient.getPerson(person.internalPersonId).then(value => { if (!cancelled) { setPersonMetadata(value); setPersonProfileError(false); setPeopleByMovie(current => Object.fromEntries(Object.entries(current).map(([movieId, people]) => [movieId, people?.map(candidate => candidate.internalPersonId === value.personId ? { ...candidate, name: value.name, providerCode: value.providerCode ?? candidate.providerCode, providerId: value.providerId, profileArtworkLocator: value.profileArtworkLocator ?? candidate.profileArtworkLocator } : candidate)]))); } }).catch(() => { if (!cancelled) { setPersonMetadata(null); setPersonProfileError(true); } }).finally(() => { if (!cancelled) setPersonProfileLoading(false); });
    return () => { cancelled = true; };
  }, [person, personMetadata]);

  const startPlayback = () => {
    const video = videoRef.current;
    if (video) {
      setPlaybackError(null);
      void Promise.resolve(video.play()).catch(() => setPlaybackError("No se pudo reproducir este archivo. Comprueba que el servidor tenga FFmpeg disponible y que el vídeo use un códec que pueda convertir."));
      const container = playerContainerRef.current;
      const requestFullscreen = container?.requestFullscreen?.bind(container) ?? document.documentElement.requestFullscreen?.bind(document.documentElement);
      if (requestFullscreen) void Promise.resolve(requestFullscreen()).catch(() => undefined);
    }
    setPlaying(true);
  };

  useEffect(() => {
    if (!playing) return;
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === "Escape") setPlaying(false); };
    document.addEventListener("keydown", onKeyDown);
    return () => { document.removeEventListener("keydown", onKeyDown); videoRef.current?.pause(); if (document.fullscreenElement) void Promise.resolve(document.exitFullscreen?.()).catch(() => undefined); };
  }, [playing]);

  const selectSection = (nextSection: LibrarySection) => { window.history.pushState({}, "", routeForSection(nextSection)?.path ?? "/"); setSection(nextSection); setSelectedMedia(null); if (nextSection !== "movies") setPlaying(false); setPerson(null); };
  const handleMetadataUpdated = () => { setMetadataByMovie({}); setCreditsByMovie({}); setPeopleByMovie({}); setReload(value => value + 1); };
  const detailTitle = String(detail?.title?.value || (selectedMedia ? presentationTitle(selectedMedia) : ""));

  if (editingMediaId) {
    const selected = media.find(item => item.id === editingMediaId);
    return <MetadataEditor mediaId={editingMediaId} title={selected ? presentationTitle(selected) : "Película"} posterUrl={selected?.effective?.posterUrl} onBack={closeEditor} />;
  }

  const drawerSection = (nextSection: LibrarySection) => { window.history.pushState({}, "", routeForSection(nextSection)?.path ?? "/"); setSection(nextSection); setSelectedMedia(null); setPerson(null); setDrawerOpen(false); };
  return <main className={`app-shell web-variant-${variant}`}>
    <header className="topbar"><button type="button" className="menu-button" aria-label="Abrir menú" aria-expanded={drawerOpen} onClick={() => setDrawerOpen(true)}>☰</button><button className="brand" type="button" onClick={() => selectSection("home")} aria-label="HISPANIA, ir a Inicio"><span className="brand-mark" aria-hidden="true">H</span><span>HISPANIA</span></button><nav className="main-navigation" aria-label="Navegación principal"><button type="button" className="nav-item" aria-current={section === "home" ? "page" : undefined} onClick={() => selectSection("home")}>Inicio</button>{navigation.map(item => <button key={item.id} type="button" className="nav-item" aria-current={section === item.id ? "page" : undefined} onClick={() => selectSection(item.id)}>{item.label}</button>)}<button type="button" className="nav-item" aria-current={section === "inventory" ? "page" : undefined} onClick={() => selectSection("inventory")}>Inventario</button></nav></header>
    {drawerOpen && <div className="drawer-layer" role="presentation" onClick={() => setDrawerOpen(false)}><aside className="app-drawer" role="dialog" aria-modal="true" aria-label="Menú HISPANIA" onClick={event => event.stopPropagation()}><div className="drawer-heading"><strong>HISPANIA</strong><button type="button" aria-label="Cerrar menú" onClick={() => setDrawerOpen(false)}>×</button></div><button type="button" className={section === "home" ? "drawer-link active" : "drawer-link"} onClick={() => drawerSection("home")}>Inicio</button><p className="drawer-label">MEDIOS</p>{navigation.map(item => <button key={item.id} type="button" className={section === item.id ? "drawer-link active" : "drawer-link"} onClick={() => drawerSection(item.id)}>{item.label}</button>)}<p className="drawer-label">ERP</p><button type="button" className={section === "inventory" ? "drawer-link active" : "drawer-link"} onClick={() => drawerSection("inventory")}>Inventario</button><p className="drawer-label">ADMINISTRACIÓN</p><button type="button" className={section === "dashboard" ? "drawer-link active" : "drawer-link"} onClick={() => drawerSection("dashboard")}>Panel de control</button>{["Bibliotecas", "Ajustes"].map(item => <button key={item} type="button" className="drawer-link" disabled aria-disabled="true">{item}</button>)}</aside></div>}
    {section === "home" && <Home onNavigate={selectSection} movies={media} onOpenMovie={openMovie} />}
    {section === "dashboard" && <AdminPanel movieCount={media.length} onMetadataUpdated={handleMetadataUpdated} />}
    {section === "inventory" && <InventoryPage />}
    {section === "movies" && !selectedMedia && !person && !initialPersonId && <section className="movie-library" aria-labelledby="movies-heading"><div className="section-heading"><div><span className="eyebrow">Tu biblioteca</span><h1 id="movies-heading">Películas</h1><p>Historias listas para volver a descubrir.</p></div><span className="catalog-count">{media.length} títulos</span></div>{loading && media.length === 0 && <div className="state-panel" role="status"><span className="state-symbol" aria-hidden="true">◌</span>Cargando películas…</div>}{error && <div className="state-panel state-error" role="status">{error} <button type="button" onClick={() => setReload(value => value + 1)}>Reintentar</button></div>}{!loading && !error && media.length === 0 && <div className="state-panel"><span className="state-symbol" aria-hidden="true">◇</span><strong>El catálogo está vacío.</strong><span>Aquí aparecerán las películas cuando estén disponibles en tu biblioteca.</span></div>}{media.length > 0 && <ul className="movie-grid">{media.map(item => <li key={item.id}><button type="button" className="movie-card" onClick={() => openMovie(item)}><Artwork key={item.effective?.posterUrl} src={item.effective?.posterUrl} alt={presentationTitle(item)} className="card-artwork" placeholderTestId="grid-artwork-placeholder" /><span className="movie-card-copy"><span className="movie-title">{presentationTitle(item)}</span>{releaseYear(item.effective?.releaseDate) && <span className="movie-year">{releaseYear(item.effective?.releaseDate)}</span>}</span></button></li>)}</ul>}</section>}
    {section === "movies" && initialPersonId && !person && <div className={`state-panel${initialPersonLoadFailed ? " state-error" : ""}`} role={initialPersonLoadFailed ? "alert" : "status"}>{initialPersonLoadFailed ? "No se pudo cargar la ficha de esta persona. Comprueba la conexión con HISPANIA e inténtalo de nuevo." : "Cargando ficha de la persona…"}</div>}
    {section === "series" && <UnavailableSection title="Series" />}{section === "collections" && <UnavailableSection title="Colecciones" />}{section === "music" && <UnavailableSection title="Música" />}
    {section === "movies" && selectedMedia && !person && <section className="movie-detail movie-experience" aria-labelledby="detail-heading">
      <button type="button" className="movie-back" onClick={() => { window.history.pushState({}, "", "/"); setSelectedMedia(null); }}>← Volver a películas</button>
      <div className="detail-hero"><Artwork key={detail?.artwork?.backdrop?.deliveryUrl} src={detail?.artwork?.backdrop?.deliveryUrl} alt="" className="detail-backdrop" placeholderTestId="backdrop-placeholder" /><div className="hero-shade" /></div>
      <div className="detail-content">
        <Artwork key={detail?.artwork?.poster?.deliveryUrl} src={detail?.artwork?.poster?.deliveryUrl} alt={detailTitle} className="detail-artwork" placeholderTestId="detail-artwork-placeholder" />
        <div className="detail-copy"><span className="eyebrow">Película</span><h2 id="detail-heading">{detailTitle}</h2>
          {detailLoading && <p role="status">Cargando detalles…</p>}{detailError && <p role="alert">Metadata no disponible.</p>}
          {detail?.originalTitle?.value && detail.originalTitle.value !== detailTitle && <p className="original-title">{detail.originalTitle.value}</p>}
          <MovieMetadataPresentation model={metadataByMovie[selectedMedia.id]} people={peopleByMovie[selectedMedia.id] ?? []} credits={creditsByMovie[selectedMedia.id] ?? []} technical={selectedMedia.technical} year={releaseYear(typeof detail?.releaseDate?.value === "string" ? detail.releaseDate.value : selectedMedia.effective?.releaseDate)} factsOnly now={clockNow} onOpenPerson={openPerson} />
          {detail?.description?.value && <p className="detail-description">{detail.description.value}</p>}
          <div className="detail-actions"><button type="button" className="play-button" onClick={startPlayback}><span aria-hidden="true">▶ </span>Reproducir</button><button type="button" className="close-detail-button" onClick={() => openEditor(selectedMedia.id)}>Editar metadata</button></div>
        </div>
      </div>
      <section className="movie-extra">
        <MovieMetadataPresentation model={metadataByMovie[selectedMedia.id]} people={peopleByMovie[selectedMedia.id] ?? []} credits={creditsByMovie[selectedMedia.id] ?? []} technical={selectedMedia.technical} onOpenPerson={openPerson} />
        <section className="cast-section" aria-labelledby="cast-heading"><div><h3 id="cast-heading">Reparto</h3></div>
          {creditsLoading && !creditsByMovie[selectedMedia.id] ? <p role="status">Cargando reparto…</p> : <div className="cast-list">{Array.from(new Map((creditsByMovie[selectedMedia.id] ?? []).filter(credit => ["cast", "actor", "actress"].includes(credit.creditType.toLowerCase())).sort((a, b) => (a.ordinal ?? Number.MAX_SAFE_INTEGER) - (b.ordinal ?? Number.MAX_SAFE_INTEGER)).map(credit => [credit.personId, credit])).values()).map(credit => { const actor = peopleByMovie[selectedMedia.id]?.find(p => p.internalPersonId === credit.personId); const name = actor?.name ?? credit.personId; return <button type="button" className="cast-bubble" key={credit.personId} onClick={() => { if (actor?.internalPersonId) openPerson(actor); }}><CastPhoto person={actor} name={name} /><strong>{name}</strong><small>{credit.character ?? ""}</small></button>; })}{!creditsLoading && !(creditsByMovie[selectedMedia.id] ?? []).some(credit => ["cast", "actor", "actress"].includes(credit.creditType.toLowerCase())) && <p>No hay reparto disponible.</p>}</div>}
        </section>
      </section>
    </section>}    {section === "movies" && person && <PersonPage person={person} metadata={personMetadata} media={media} profileLoading={personProfileLoading} onBack={() => { window.history.pushState({}, "", selectedMedia ? `/movies/${encodeURIComponent(selectedMedia.id)}` : "/"); setPerson(null); setPersonMetadata(null); }} onOpenMovie={openMovie} />}
    {selectedMedia && <div ref={playerContainerRef} className={`fullscreen-player${playing ? " is-open" : ""}`} role={playing ? "dialog" : undefined} aria-modal={playing ? "true" : undefined} aria-hidden={!playing} aria-label={playing ? `Reproductor: ${detailTitle}` : undefined} inert={!playing}>{playbackError && playing && <p className="player-error" role="alert">{playbackError}</p>}<video ref={videoRef} controls playsInline preload={playing ? "metadata" : "none"} src={streamUrl(selectedMedia.id)} onError={() => setPlaybackError("No se pudo reproducir este archivo. Comprueba que el servidor tenga FFmpeg disponible y que el vídeo use un códec que pueda convertir.")}>Tu navegador no admite la reproducción de vídeo.</video><button type="button" className="fullscreen-close" aria-label="Cerrar reproductor" onClick={() => setPlaying(false)}>×</button></div>}
    <footer className="tmdb-attribution">This product uses the TMDB API but is not endorsed or certified by TMDB.</footer>
  </main>;
}
