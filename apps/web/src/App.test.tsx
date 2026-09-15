import { fireEvent, render, screen } from "@testing-library/react";
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
    await user.click(screen.getByRole("button", { name: "Play" }));
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
    await user.click(screen.getByRole("button", { name: "Play" }));
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
    expect(screen.getByRole("button", { name: "Detail title" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Play" }));
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

  it("renders an error when the catalog request fails", async () => {
    vi.stubGlobal("fetch", vi.fn(() => catalogResponse(null, false)));

    render(<App />);

    expect(await screen.findByRole("alert")).toHaveTextContent("No se ha podido cargar el catálogo.");
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
});
