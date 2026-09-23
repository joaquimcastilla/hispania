import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ContentCarousel } from "./ContentCarousel";

const items = ["Uno", "Dos", "Tres"].map(label => ({ id: label, label, onActivate: vi.fn() }));

function setOverflow(viewport: HTMLElement, scrollWidth = 900, clientWidth = 300) {
  Object.defineProperties(viewport, {
    clientWidth: { configurable: true, value: clientWidth },
    scrollWidth: { configurable: true, value: scrollWidth },
    scrollBy: { configurable: true, value: vi.fn() },
  });
  fireEvent(window, new Event("resize"));
}

describe("ContentCarousel", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("renders one accessible row and activates the whole library card", async () => {
    const user = userEvent.setup();
    render(<ContentCarousel title="Mis contenidos" items={items} variant="library" />);
    await user.click(screen.getByRole("button", { name: /Uno/ }));
    expect(items[0].onActivate).toHaveBeenCalledOnce();
    expect(screen.getByRole("heading", { name: "Mis contenidos" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Dos/ })).toHaveClass("content-card-library");
  });

  it("enables and disables arrows at the actual scroll limits", () => {
    render(<ContentCarousel title="Reciente" items={items} />);
    const viewport = screen.getByRole("group", { name: "Reciente" });
    setOverflow(viewport);
    const left = screen.getByRole("button", { name: "Desplazar Reciente a la izquierda" });
    const right = screen.getByRole("button", { name: "Desplazar Reciente a la derecha" });
    expect(left).toBeDisabled();
    expect(right).toBeEnabled();
    Object.defineProperty(viewport, "scrollLeft", { configurable: true, value: 600 });
    fireEvent.scroll(viewport);
    expect(left).toBeEnabled();
    expect(right).toBeDisabled();
  });

  it("moves only the selected carousel and supports arrow-key scrolling", async () => {
    const user = userEvent.setup();
    render(<><ContentCarousel title="Uno" items={items} /><ContentCarousel title="Dos" items={items} /></>);
    const viewportOne = screen.getByRole("group", { name: "Uno" });
    const viewportTwo = screen.getByRole("group", { name: "Dos" });
    setOverflow(viewportOne);
    setOverflow(viewportTwo);
    const scrollOne = vi.fn();
    const scrollTwo = vi.fn();
    Object.defineProperty(viewportOne, "scrollBy", { configurable: true, value: scrollOne });
    Object.defineProperty(viewportTwo, "scrollBy", { configurable: true, value: scrollTwo });
    await user.click(within(screen.getByRole("region", { name: "Uno" })).getByRole("button", { name: "Desplazar Uno a la derecha" }));
    expect(scrollOne).toHaveBeenCalledOnce();
    expect(scrollTwo).not.toHaveBeenCalled();
    viewportTwo.focus();
    await user.keyboard("{ArrowRight}");
    expect(scrollTwo).toHaveBeenCalledOnce();
  });

  it("uses immediate scrolling when reduced motion is requested", () => {
    vi.stubGlobal("matchMedia", vi.fn(() => ({ matches: true } as MediaQueryList)));
    render(<ContentCarousel title="Accesible" items={items} />);
    const viewport = screen.getByRole("group", { name: "Accesible" });
    setOverflow(viewport);
    const scroll = vi.fn();
    Object.defineProperty(viewport, "scrollBy", { configurable: true, value: scroll });
    fireEvent.click(screen.getByRole("button", { name: "Desplazar Accesible a la derecha" }));
    expect(scroll).toHaveBeenCalledWith({ left: 255, behavior: "auto" });
  });

  it("supports landscape and poster layouts with an artwork placeholder", () => {
    render(<><ContentCarousel title="Seguir viendo" variant="landscape" items={[{ id: "continue", label: "En curso", progress: 42 }]} /><ContentCarousel title="Recientes" variant="poster" items={[{ id: "poster", label: "Sin póster", description: "2024" }]} /></>);
    expect(screen.getByRole("button", { name: /En curso/ })).toHaveClass("content-card-landscape");
    expect(screen.getByRole("progressbar", { name: "Progreso de En curso" })).toHaveAttribute("aria-valuenow", "42");
    expect(screen.getByRole("button", { name: /Sin póster/ })).toHaveClass("content-card-poster");
    expect(within(screen.getByRole("button", { name: /Sin póster/ })).getByText("✦")).toBeInTheDocument();
    expect(screen.getByText("2024")).toBeInTheDocument();
  });

  it("hides arrows when a single card fits and displays the empty state", () => {
    const { unmount } = render(<ContentCarousel title="Ajustado" items={[items[0]]} />);
    expect(screen.queryByRole("button", { name: "Desplazar Ajustado a la izquierda" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Desplazar Ajustado a la derecha" })).not.toBeInTheDocument();
    unmount();
    render(<ContentCarousel title="Vacío" items={[]} />);
    expect(screen.queryByRole("button", { name: "Desplazar Vacío a la izquierda" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Desplazar Vacío a la derecha" })).not.toBeInTheDocument();
    expect(screen.getByText("Todavía no hay contenido.")).toBeInTheDocument();
  });
});
