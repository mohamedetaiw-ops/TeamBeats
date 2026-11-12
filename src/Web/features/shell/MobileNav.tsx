"use client";

import { useEffect, useId, useRef, useState } from "react";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import type { NavLink } from "@/lib/shell/types";
import { Icon } from "@/components/ui/Icon";
import { iconFor, isCurrent } from "./GlobalMenu";

/** Slots in the bar, "More" included (spec 030 FR-005). */
const slots = 5;

/**
 * Bottom navigation on phones (spec 030 US5, FR-005): the items the experience administrator chose, with labels, and
 * "More" for the rest of the global menu. When "More" is needed it takes the last slot, so the bar never holds more
 * than five targets. The bar shows only on narrow screens and mirrors in RTL through logical properties.
 */
export function MobileNav({ items, all }: { items: NavLink[]; all: NavLink[] }) {
  const t = useTranslations("app.nav");
  const path = usePathname() ?? "/";
  const [open, setOpen] = useState(false);
  const panel = useId();
  const nav = useRef<HTMLElement>(null);
  const button = useRef<HTMLButtonElement>(null);

  const chosen = new Set(items.map((i) => i.nodeId));
  const rest = all.filter((l) => !chosen.has(l.nodeId));
  const shown = rest.length > 0 || items.length > slots ? items.slice(0, slots - 1) : items;
  const more = [...items.slice(shown.length), ...rest];

  useEffect(() => {
    if (!open) return;
    const away = (e: PointerEvent) => {
      if (nav.current && !nav.current.contains(e.target as Node)) setOpen(false);
    };
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setOpen(false);
        button.current?.focus();
      }
    };
    document.addEventListener("pointerdown", away);
    document.addEventListener("keydown", key);
    return () => {
      document.removeEventListener("pointerdown", away);
      document.removeEventListener("keydown", key);
    };
  }, [open]);

  if (shown.length === 0) return null;
  return (
    <nav ref={nav} className="mobile-nav" aria-label={t("label")}>
      {more.length > 0 ? (
        <ul id={panel} className="mobile-nav-more" hidden={!open}>
          {more.map((l) => (
            <li key={l.nodeId}>
              <a href={l.href} aria-current={isCurrent(l.href, path) ? "page" : undefined}>
                <Icon name={iconFor(l.href)} size={18} />
                <bdi>{l.label}</bdi>
              </a>
            </li>
          ))}
        </ul>
      ) : null}
      <ul className="mobile-nav-bar">
        {shown.map((l) => (
          <li key={l.nodeId}>
            <a href={l.href} aria-current={isCurrent(l.href, path) ? "page" : undefined}>
              <Icon name={iconFor(l.href)} size={20} />
              <bdi>{l.label}</bdi>
            </a>
          </li>
        ))}
        {more.length > 0 ? (
          <li>
            <button ref={button} type="button" aria-expanded={open} aria-controls={panel} onClick={() => setOpen((v) => !v)}>
              <Icon name="more" size={20} />
              <span>{t("more")}</span>
            </button>
          </li>
        ) : null}
      </ul>
    </nav>
  );
}
