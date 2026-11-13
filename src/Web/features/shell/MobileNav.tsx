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
  // The tree of each chosen item, so pages under it stay reachable from More (spec 008 FR-063).
  const tree = (l: NavLink) => all.find((a) => a.nodeId === l.nodeId) ?? l;
  const parents = items.filter((i) => (tree(i).children?.length ?? 0) > 0);
  const needMore = rest.length > 0 || parents.length > 0 || items.length > slots;
  const shown = needMore ? items.slice(0, slots - 1) : items;
  const leftOver = items.slice(shown.length).map(tree);
  // More holds the whole global menu that the bar does not: items left out of the bar, the rest of the menu, and the
  // pages under the items in the bar, so the phone needs no separate Menu row.
  const more = [...leftOver, ...rest, ...parents.filter((p) => !leftOver.some((l) => l.nodeId === p.nodeId)).map(tree)];

  useEffect(() => {
    if (!open) return;
    // The panel sits above the bar, before the button in the page order, so opening moves focus to its first link.
    nav.current?.querySelector<HTMLAnchorElement>(".mobile-nav-more a")?.focus();
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
            <MoreItem key={l.nodeId} link={l} path={path} />
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

function MoreItem({ link, path }: { link: NavLink; path: string }) {
  return (
    <li>
      <a href={link.href} aria-current={isCurrent(link.href, path) ? "page" : undefined}>
        <Icon name={iconFor(link.href)} size={18} />
        <bdi>{link.label}</bdi>
      </a>
      {link.children?.length ? (
        <ul className="mobile-nav-more-children">
          {link.children.map((c) => (
            <MoreItem key={c.nodeId} link={c} path={path} />
          ))}
        </ul>
      ) : null}
    </li>
  );
}
