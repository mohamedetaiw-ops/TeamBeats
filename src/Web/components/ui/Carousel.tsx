"use client";

import { Children, useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Icon } from "./Icon";

/**
 * Carousel (spec 036 DLS-038): a row of cards that scrolls sideways and snaps to each card. It never moves by itself.
 * The previous and next buttons are always visible and turn off at the ends; the row also scrolls with touch, a
 * trackpad and, when it has focus, the arrow keys. Every card stays in the page for screen readers and for find in
 * page, as items of one named list.
 */
export function Carousel({ label, children }: { label: string; children: ReactNode }) {
  const t = useTranslations("ui.controls");
  const track = useRef<HTMLUListElement>(null);
  const [ends, setEnds] = useState({ start: true, end: false });

  const measure = useCallback(() => {
    const el = track.current;
    if (!el) return;
    // In a right-to-left row scrollLeft counts down from zero, so the distance is compared without its sign.
    const offset = Math.abs(el.scrollLeft);
    setEnds({ start: offset <= 1, end: offset >= el.scrollWidth - el.clientWidth - 1 });
  }, []);

  useEffect(() => {
    measure();
    window.addEventListener("resize", measure);
    return () => window.removeEventListener("resize", measure);
  }, [measure]);

  function step(direction: 1 | -1) {
    const el = track.current;
    const card = el?.querySelector<HTMLElement>(":scope > li");
    if (!el || !card) return;
    const rtl = getComputedStyle(el).direction === "rtl";
    const gap = Number.parseFloat(getComputedStyle(el).columnGap) || 0;
    el.scrollBy({ left: direction * (rtl ? -1 : 1) * (card.offsetWidth + gap), behavior: "smooth" });
  }

  return (
    <div className="ui-carousel">
      <div className="ui-carousel-controls">
        <button type="button" className="button button-secondary ui-icon-button ui-carousel-previous" data-size="sm" aria-label={t("previousSlide")} disabled={ends.start} onClick={() => step(-1)}>
          <Icon name="chevron-forward" size={18} />
        </button>
        <button type="button" className="button button-secondary ui-icon-button" data-size="sm" aria-label={t("nextSlide")} disabled={ends.end} onClick={() => step(1)}>
          <Icon name="chevron-forward" size={18} />
        </button>
      </div>
      {/* The scrolling list takes focus so the arrow keys can scroll it when its cards hold nothing focusable. */}
      <ul ref={track} className="ui-carousel-track" tabIndex={0} aria-label={label} onScroll={measure}>
        {Children.map(children, (child) => (
          <li>{child}</li>
        ))}
      </ul>
    </div>
  );
}
