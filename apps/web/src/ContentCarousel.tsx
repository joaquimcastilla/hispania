import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";

export type ContentCarouselVariant = "library" | "landscape" | "poster";

export type ContentCarouselItem = {
  id: string;
  label: string;
  description?: string;
  className?: string;
  artworkUrl?: string | null;
  progress?: number;
  onActivate?: () => void;
};

function CarouselArtwork({ src }: { src?: string | null }) {
  const [failed, setFailed] = useState(false);
  if (!src || failed) return <span className="artwork-placeholder" aria-hidden="true">✦</span>;
  return <img src={src} alt="" loading="lazy" onError={() => setFailed(true)} />;
}

export function ContentCarousel({
  title,
  items,
  variant = "library",
  ariaLabel,
}: {
  title: string;
  items: ContentCarouselItem[];
  variant?: ContentCarouselVariant;
  ariaLabel?: string;
}) {
  const viewport = useRef<HTMLDivElement>(null);
  const headingId = useId();
  const [canLeft, setCanLeft] = useState(false);
  const [canRight, setCanRight] = useState(false);
  const [hasOverflow, setHasOverflow] = useState(false);

  useEffect(() => {
    const element = viewport.current;
    if (!element) return;

    const updateEdges = () => {
      const overflow = element.scrollWidth > element.clientWidth + 1;
      setHasOverflow(overflow);
      setCanLeft(overflow && element.scrollLeft > 1);
      setCanRight(overflow && element.scrollLeft + element.clientWidth < element.scrollWidth - 1);
    };

    updateEdges();
    const resize = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(updateEdges);
    resize?.observe(element);
    element.addEventListener("scroll", updateEdges, { passive: true });
    window.addEventListener("resize", updateEdges);
    return () => {
      resize?.disconnect();
      element.removeEventListener("scroll", updateEdges);
      window.removeEventListener("resize", updateEdges);
    };
  }, [items.length, variant]);

  const move = (direction: number) => {
    const element = viewport.current;
    if (!element) return;
    const amount = Math.max(element.clientWidth * 0.85, 240) * direction;
    const reduced = typeof window.matchMedia === "function" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    element.scrollBy({ left: amount, behavior: reduced ? "auto" : "smooth" });
  };

  const handleViewportKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === "ArrowRight" || event.key === "ArrowLeft") {
      event.preventDefault();
      move(event.key === "ArrowRight" ? 1 : -1);
    }
  };

  return (
    <section className={`content-carousel carousel-${variant}`} aria-labelledby={headingId}>
      <div className="carousel-heading">
        <h2 id={headingId}>{title}</h2>
        {hasOverflow && (
          <div className="carousel-controls" aria-label={`Controles de ${title}`}>
            <button type="button" aria-label={`Desplazar ${title} a la izquierda`} onClick={() => move(-1)} disabled={!canLeft}>←</button>
            <button type="button" aria-label={`Desplazar ${title} a la derecha`} onClick={() => move(1)} disabled={!canRight}>→</button>
          </div>
        )}
      </div>
      {items.length === 0 ? (
        <p className="carousel-empty">Todavía no hay contenido.</p>
      ) : (
        <div className="carousel-viewport" ref={viewport} role="group" tabIndex={0} aria-label={ariaLabel ?? title} onKeyDown={handleViewportKeyDown}>
          <div className="carousel-track">
            {items.map(item => (
              <button key={item.id} type="button" className={`content-card content-card-${variant} ${item.className ?? ""}`} onClick={item.onActivate}>
                {variant === "library" ? (
                  <>
                    <span className="content-card-gradient" />
                    <span className="content-card-label">{item.label}</span>
                    {item.description && <small>{item.description}</small>}
                  </>
                ) : (
                  <>
                    <span className="carousel-artwork-frame">
                      <CarouselArtwork src={item.artworkUrl} />
                      {variant === "landscape" && item.progress !== undefined && <span className="carousel-progress" role="progressbar" aria-label={`Progreso de ${item.label}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={item.progress}><span style={{ width: `${Math.max(0, Math.min(100, item.progress))}%` }} /></span>}
                    </span>
                    <span className="carousel-item-copy">
                      <span className="carousel-item-title">{item.label}</span>
                      {item.description && <small>{item.description}</small>}
                    </span>
                  </>
                )}
              </button>
            ))}
          </div>
        </div>
      )}
    </section>
  );
}
