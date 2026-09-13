import { useEffect, useState } from "react";

type MediaListItem = {
  id: string;
  displayName: string;
};

const catalogUrl = "/api/v1/media";

export function streamUrl(mediaId: string): string {
  return `/api/v1/media/${encodeURIComponent(mediaId)}/stream`;
}

export function App() {
  const [media, setMedia] = useState<MediaListItem[]>([]);
  const [selectedMedia, setSelectedMedia] = useState<MediaListItem | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    async function loadCatalog() {
      try {
        const response = await fetch(catalogUrl, { signal: controller.signal });
        if (!response.ok) {
          throw new Error("Catalog request failed.");
        }

        const catalog: unknown = await response.json();
        setMedia(Array.isArray(catalog) ? catalog as MediaListItem[] : []);
      } catch (cause) {
        if (!controller.signal.aborted) {
          setError("No se ha podido cargar el catálogo.");
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    }

    void loadCatalog();
    return () => controller.abort();
  }, []);

  return (
    <main className="app-shell">
      <header>
        <h1>HISPANIA</h1>
      </header>

      <section aria-labelledby="movies-heading">
        <h2 id="movies-heading">Movies</h2>

        {loading && <p role="status">Cargando películas…</p>}
        {error && <p role="alert">{error}</p>}
        {!loading && !error && media.length === 0 && <p>El catálogo está vacío.</p>}

        {!loading && !error && media.length > 0 && (
          <ul className="movie-list">
            {media.map((item) => (
              <li key={item.id}>
                <button
                  type="button"
                  className="movie-button"
                  aria-pressed={selectedMedia?.id === item.id}
                  onClick={() => setSelectedMedia(item)}
                >
                  <span>{item.displayName}</span>
                  <span aria-hidden="true">Play</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      {selectedMedia && (
        <section className="player" aria-labelledby="player-heading">
          <h2 id="player-heading">{selectedMedia.displayName}</h2>
          <video controls preload="metadata" src={streamUrl(selectedMedia.id)}>
            Your browser does not support video playback.
          </video>
        </section>
      )}
    </main>
  );
}
