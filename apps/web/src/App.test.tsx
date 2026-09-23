import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";

function catalogResponse(body: unknown, ok = true) {
  return Promise.resolve({ ok, json: async () => body } as Response);
}

describe("App", () => {
  beforeEach(() => {
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    vi.spyOn(HTMLMediaElement.prototype, "pause").mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    window.history.replaceState({}, "", "/");
  });

  it("shows the home rows in order and keeps the library card navigable", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([])));
    render(<App />);
    await user.click(screen.getByRole("button", { name: "Inicio" }));
    const headings = screen.getAllByRole("heading").map(heading => heading.textContent);
    expect(headings.indexOf("Mis contenidos")).toBeGreaterThanOrEqual(0);
    expect(headings.indexOf("Mis contenidos")).toBeLessThan(headings.indexOf("Seguir viendo"));
    expect(headings.indexOf("Seguir viendo")).toBeLessThan(headings.indexOf("Reciente en Películas"));
    const moviesCard = within(screen.getByRole("region", { name: "Mis contenidos" })).getByRole("button", { name: "Películas" });
    expect(moviesCard).toBeInTheDocument();
    await user.click(moviesCard);
    expect(await screen.findByRole("heading", { name: "Películas", level: 1 })).toBeInTheDocument();
  });

  it("opens Movie Detail from a recent poster and keeps the local artwork placeholder", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([{ id: "recent-id", displayName: "Película reciente", effective: { title: "Película reciente", releaseDate: "2024-03-01", posterUrl: null } }])));
    render(<App />);
    await user.click(screen.getByRole("button", { name: "Inicio" }));
    const recentCard = await screen.findByRole("button", { name: /Película reciente/ });
    expect(within(recentCard).getByText("✦")).toBeInTheDocument();
    expect(within(recentCard).getByText("2024")).toBeInTheDocument();
    await user.click(recentCard);
    expect(await screen.findByRole("heading", { name: "Película reciente", level: 2 })).toBeInTheDocument();
  });

  it("opens a movie-specific page, fullscreen playback, and a person page with other local films", async () => {
    const user = userEvent.setup();
    const movieId = "11111111-1111-4111-8111-111111111111";
    const secondId = "22222222-2222-4222-8222-222222222222";
    const personId = "33333333-3333-4333-8333-333333333333";
    const movies = [{ id: movieId, displayName: "First Movie" }, { id: secondId, displayName: "Second Movie" }];
    const editModel = (id: string) => ({ mediaId: id, workId: `work-${id}`, settings: {}, locks: [], fields: [], collections: [], people: [{ providerId: "tmdb:42", name: "Actor Name", internalPersonId: personId }], credits: [{ personId, workId: `work-${id}`, creditType: "Actor", character: "Hero", job: null, ordinal: 0, origin: "ExternalProvider", providerCode: "tmdb", observedAt: "2026-01-01T00:00:00Z" }] });
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url === "/api/v1/media") return catalogResponse(movies);
      if (url.endsWith("/metadata/edit")) return catalogResponse(editModel(url.includes(movieId) ? movieId : secondId));
      if (url.endsWith("/metadata/tmdb")) return catalogResponse({ provider: "tmdb", candidate: null, confirmed: null, refresh: null });
      if (url.endsWith(`/api/v1/people/${personId}`)) return catalogResponse({ personId, providerCode: "tmdb", providerId: "42", name: "Actor Name", biography: "Biografía local de prueba", profileArtworkLocator: "/actor.jpg", filmography: [{ mediaId: secondId, title: "Second Movie", displayName: "Second Movie", releaseDate: null, posterUrl: null }] });
      if (url.endsWith("/metadata")) return catalogResponse({ title: { value: url.includes(movieId) ? "First Movie" : "Second Movie" }, originalTitle: {}, description: {}, releaseDate: {}, artwork: { poster: {}, backdrop: {} } });
      throw new Error(`Unexpected request: ${url}`);
    }));
    render(<App />);
    await user.click(await screen.findByRole("button", { name: "First Movie" }));
    expect(window.location.pathname).toBe(`/movies/${movieId}`);
    await user.click(await screen.findByRole("button", { name: "Reproducir" }));
    expect(screen.getByRole("dialog", { name: "Reproductor: First Movie" })).toBeInTheDocument();
    expect(document.querySelector(".fullscreen-player video")).toHaveAttribute("src", `/api/v1/media/${movieId}/stream`);
    fireEvent.error(document.querySelector(".fullscreen-player video")!);
    expect(screen.getByRole("alert")).toHaveTextContent("No se pudo reproducir este archivo");
    await user.click(screen.getByRole("button", { name: "Cerrar reproductor" }));
    expect(HTMLMediaElement.prototype.pause).toHaveBeenCalled();
    await user.click(await screen.findByRole("button", { name: /Actor Name/ }));
    expect(window.location.pathname).toBe(`/people/${personId}`);
    expect(await screen.findByRole("heading", { name: "Actor Name" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Second Movie" })).toBeInTheDocument();
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

    expect(window.location.pathname).toBe("/movies/media-id");
    expect(screen.getByRole("heading", { name: "Big Buck Bunny", level: 2 })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reproducir" }));
    expect(document.querySelector("video")).toHaveAttribute("src", "/api/v1/media/media-id/stream");
  });

  it("uses effective title, year, and posterUrl in the library grid", async () => {
    const posterUrl = "/api/v1/artwork/artwork-id?variant=poster-card";
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([{ id: "media-id", displayName: "local-file", effective: { title: "Seduction and Snacks", releaseDate: "2021-01-01", posterUrl } }])));
    render(<App />);
    expect(await screen.findByRole("button", { name: /Seduction and Snacks/ })).toBeInTheDocument();
    expect(screen.getByText("2021")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Seduction and Snacks" })).toHaveAttribute("src", posterUrl);
    expect(screen.getByRole("img", { name: "Seduction and Snacks" })).toHaveAttribute("loading", "lazy");
  });

  it("keeps a per-card placeholder when posterUrl is missing or the image fails", async () => {
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([
      { id: "with-poster", displayName: "With poster", effective: { title: "With poster", posterUrl: "/api/v1/artwork/a?variant=poster-card" } },
      { id: "without-poster", displayName: "Without poster", effective: { title: "Without poster", posterUrl: null } }
    ])));
    render(<App />);

    const image = await screen.findByRole("img", { name: "With poster" });
    expect(screen.getByTestId("grid-artwork-placeholder")).toBeInTheDocument();
    fireEvent.error(image);
    expect(screen.queryByRole("img", { name: "With poster" })).not.toBeInTheDocument();
    expect(screen.getAllByTestId("grid-artwork-placeholder")).toHaveLength(2);
    expect(screen.getByText("With poster")).toBeInTheDocument();
    expect(screen.getByText("Without poster")).toBeInTheDocument();
  });

  it("loads effective metadata detail while keeping Play available", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn((url: string) => url.endsWith("/metadata")
      ? catalogResponse({ title: { value: "Title" }, originalTitle: { value: "Original" }, description: { value: "Description" }, releaseDate: { value: "2021-01-01" }, artwork: { poster: {}, backdrop: {} } })
      : catalogResponse([{ id: "media-id", displayName: "local-file", effective: { title: "Title" } }])));
    render(<App />);
    await user.click(await screen.findByRole("button", { name: "Title" }));
    expect(await screen.findByText("Description")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reproducir" }));
    expect(document.querySelector("video")).toHaveAttribute("src", "/api/v1/media/media-id/stream");
  });

  it("renders the detail poster deliveryUrl and falls back locally on error", async () => {
    const user = userEvent.setup();
    const detailPosterUrl = "/api/v1/artwork/detail-artwork?variant=poster-card";
    vi.stubGlobal("fetch", vi.fn((url: string) => url.endsWith("/metadata")
      ? catalogResponse({ title: { value: "Detail title" }, originalTitle: {}, description: {}, releaseDate: {}, artwork: { poster: { deliveryUrl: detailPosterUrl }, backdrop: { deliveryUrl: "/api/v1/artwork/backdrop?variant=backdrop-detail" } } })
      : catalogResponse([{ id: "media-id", displayName: "local-file", effective: { title: "Detail title", posterUrl: null } }])));
    render(<App />);
    await user.click(await screen.findByRole("button", { name: "Detail title" }));

    const detailImage = await screen.findByRole("img", { name: "Detail title" });
    expect(detailImage).toHaveClass("detail-artwork");
    expect(detailImage).toHaveAttribute("src", detailPosterUrl);
    expect(document.querySelector("img.detail-backdrop")).toHaveAttribute("src", "/api/v1/artwork/backdrop?variant=backdrop-detail");
    fireEvent.error(detailImage);
    expect(screen.queryByRole("img", { name: "Detail title" })).not.toBeInTheDocument();
    expect(screen.getByTestId("detail-artwork-placeholder")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Detail title", level: 2 })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reproducir" }));
    expect(document.querySelector("video")).toHaveAttribute("src", "/api/v1/media/media-id/stream");
  });

  it("uses a detail placeholder when metadata has no poster deliveryUrl", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn((url: string) => url.endsWith("/metadata")
      ? catalogResponse({ title: { value: "No detail poster" }, originalTitle: {}, description: {}, releaseDate: {}, artwork: { poster: {}, backdrop: {} } })
      : catalogResponse([{ id: "media-id", displayName: "local-file", effective: { title: "No detail poster" } }])));
    render(<App />);
    await user.click(await screen.findByRole("button", { name: "No detail poster" }));
    expect(await screen.findByTestId("detail-artwork-placeholder")).toBeInTheDocument();
    expect(screen.getByTestId("backdrop-placeholder")).toBeInTheDocument();
  });

  it("recovers the catalog after an unavailable API and offers a retry", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockResolvedValueOnce(await catalogResponse(null, false)).mockResolvedValueOnce(await catalogResponse([{ id: "media-id", displayName: "Recovered movie" }]));
    vi.stubGlobal("fetch", fetchMock);
    render(<App />);
    expect(await screen.findByText(/El servidor está iniciándose/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reintentar" }));
    expect(await screen.findByRole("button", { name: "Recovered movie" })).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("shows TMDB attribution", async () => {
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([])));
    render(<App />);
    expect(await screen.findByText("This product uses the TMDB API but is not endorsed or certified by TMDB.")).toBeInTheDocument();
  });

  it("keeps every library section visible and shows honest unavailable states", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([])));
    render(<App />);

    expect(screen.getByRole("navigation", { name: "Navegación principal" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Películas" })).toHaveAttribute("aria-current", "page");
    for (const label of ["Series", "Colecciones", "Música"]) {
      await user.click(screen.getByRole("button", { name: label }));
      expect(await screen.findByRole("heading", { name: label, level: 1 })).toBeInTheDocument();
      expect(screen.getByText("Aún no disponible")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: label })).toHaveAttribute("aria-current", "page");
    }
  });

  it("renders provider metadata, cast and crew without the old TMDB admin panel", async () => {
    const user = userEvent.setup();
    const movieId = "media-id";
    const personId = "actor-id";
    const editModel = { mediaId: movieId, workId: "work-id", settings: {}, locks: [], fields: [
      { fieldCode: "runtime-minutes", effective: 123 }, { fieldCode: "community-rating", effective: 8.2 }, { fieldCode: "content-rating", effective: "12" }, { fieldCode: "tagline", effective: "Una gran historia" }, { fieldCode: "imdb-id", effective: "tt123" }
    ], collections: [ { collectionCode: "genres", effective: ["Drama"] }, { collectionCode: "studios", effective: ["Studio A"] } ], people: [
      { providerId: "101", name: "Actor Name", internalPersonId: personId, providerCode: "tmdb", profileArtworkLocator: "/actor.jpg" },
      { providerId: "102", name: "Director Name", internalPersonId: "director-id", providerCode: "tmdb" },
      { providerId: "103", name: "Writer Name", internalPersonId: "writer-id", providerCode: "tmdb" }
    ], credits: [
      { personId, creditType: "cast", character: "Hero", ordinal: 0 },
      { personId: "director-id", creditType: "director", character: null, job: "Director", ordinal: 1 },
      { personId: "writer-id", creditType: "writer", character: null, job: "Writer", ordinal: 2 }
    ] };
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url === "/api/v1/media") return catalogResponse([{ id: movieId, displayName: "Local movie", technical: { durationSeconds: 7380, video: { codec: "h264", width: 1920, height: 1080, bitDepth: 8 }, audioTracks: [] } }]);
      if (url.endsWith("/metadata/edit")) return catalogResponse(editModel);
      if (url.endsWith("/api/v1/people/director-id")) return catalogResponse({ personId: "director-id", providerCode: "tmdb", providerId: "102", name: "Director Name", filmography: [{ mediaId: movieId, title: "Local movie", displayName: "Local movie", releaseDate: "2021-01-01" }] });
      if (url.endsWith("/metadata")) return catalogResponse({ title: { value: "Local movie" }, originalTitle: {}, description: { value: "Synopsis" }, releaseDate: { value: "2021-01-01" }, artwork: { poster: {}, backdrop: {} } });
      throw new Error(`Unexpected request: ${url}`);
    }));
    render(<App />);
    await user.click(await screen.findByRole("button", { name: "Local movie" }));
    expect(await screen.findByText("2 h 3 min")).toBeInTheDocument();
    expect(screen.getByText("★ 8.2")).toBeInTheDocument();
    expect(screen.getByText("12")).toBeInTheDocument();
    expect(screen.getByText("Una gran historia")).toBeInTheDocument();
    expect(screen.getByText("Director Name")).toBeInTheDocument();
    expect(screen.getByText("Writer Name")).toBeInTheDocument();
    expect(screen.queryByText("Reparto y equipo")).not.toBeInTheDocument();
    expect(screen.getByText(/Termina a las/)).toBeInTheDocument();
    expect(screen.getByText("Studio A")).toBeInTheDocument();
    expect(screen.getByText("Drama")).toBeInTheDocument();
    expect(await screen.findByRole("img", { name: "Actor Name" })).toHaveAttribute("src", "https://image.tmdb.org/t/p/w185/actor.jpg");
    expect(screen.queryByText("Metadata", { selector: ".eyebrow" })).not.toBeInTheDocument();
    expect(screen.queryByText(/TMDB confirmado:/)).not.toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Subtítulos" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Director Name" }));
    expect(await screen.findByRole("heading", { name: "Director Name" })).toBeInTheDocument();
    expect(screen.getByText(/TMDB no proporciona una biografía/)).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Películas de Director Name" })).toBeInTheDocument();
  });
  it("opens and closes the accessible global drawer with Escape and outside click", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([])));
    render(<App />);
    await user.click(screen.getByRole("button", { name: "Abrir menú" }));
    expect(screen.getByRole("dialog", { name: "Menú HISPANIA" })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Menú HISPANIA" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Abrir menú" }));
    fireEvent.click(screen.getByRole("presentation"));
    expect(screen.queryByRole("dialog", { name: "Menú HISPANIA" })).not.toBeInTheDocument();
  });

  it("navigates real sections from the drawer and keeps unavailable sections honest", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse([])));
    render(<App />);
    await user.click(screen.getByRole("button", { name: "Abrir menú" }));
    await user.click(within(screen.getByRole("dialog", { name: "Menú HISPANIA" })).getByRole("button", { name: "Series" }));
    expect(screen.getByRole("heading", { name: "Series" })).toBeInTheDocument();
    expect(screen.getByText("Aún no disponible")).toBeInTheDocument();
  });
});
