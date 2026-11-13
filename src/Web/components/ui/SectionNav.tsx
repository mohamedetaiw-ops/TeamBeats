"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";

/**
 * Section tabs (docs/design-system/components.md, DLS-080): the pill segmented control under a page header that moves
 * between the pages of one area, such as My policies and the register. Each item is a link and the current one is marked
 * with aria-current. When the tabs do not fit, the row scrolls sideways inside itself, a fade shows at each edge that
 * hides more tabs, and the current tab is scrolled into view, so every tab stays reachable on a phone.
 */
export function SectionNav({
  label,
  items,
  current,
}: {
  label: string;
  items: readonly { key: string; href: string; label: string }[];
  current: string | null;
}) {
  const ref = useRef<HTMLElement>(null);
  const [more, setMore] = useState<{ start: boolean; end: boolean }>({ start: false, end: false });

  useEffect(() => {
    const nav = ref.current;
    if (!nav) return;
    const measure = () => {
      // scrollLeft runs negative in right to left, so its size is what matters.
      const offset = Math.abs(nav.scrollLeft);
      const next = { start: offset > 1, end: offset + nav.clientWidth < nav.scrollWidth - 1 };
      setMore((prev) => (prev.start === next.start && prev.end === next.end ? prev : next));
    };
    const active = nav.querySelector<HTMLElement>('[aria-current="page"]');
    if (active && nav.scrollWidth > nav.clientWidth) {
      const navBox = nav.getBoundingClientRect();
      const box = active.getBoundingClientRect();
      if (box.left < navBox.left || box.right > navBox.right) nav.scrollLeft += box.left - navBox.left - (navBox.width - box.width) / 2;
    }
    measure();
    nav.addEventListener("scroll", measure, { passive: true });
    const observer = new ResizeObserver(measure);
    observer.observe(nav);
    return () => {
      nav.removeEventListener("scroll", measure);
      observer.disconnect();
    };
  }, [current]);

  return (
    <nav ref={ref} aria-label={label} className="ui-section-nav" data-more-start={more.start || undefined} data-more-end={more.end || undefined}>
      <ul>
        {items.map((item) => (
          <li key={item.key}>
            <Link href={item.href} aria-current={current === item.key ? "page" : undefined}>
              {item.label}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
