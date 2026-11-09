"use client";

import { useEffect, useId, useRef, useState, type FocusEvent, type KeyboardEvent } from "react";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import type { NavLink } from "@/lib/shell/types";
import { Icon, type IconName } from "@/components/ui/Icon";

/** A picture for a menu entry, chosen from where it goes; labels and order still come from the API. */
const iconPrefixes: [string, IconName][] = [
  ["/services", "services"],
  ["/requests", "inbox"],
  ["/tasks", "tasks"],
  ["/people", "people"],
  ["/documents", "files"],
  ["/kb", "book"],
  ["/policies", "policy"],
  ["/content", "news"],
  ["/news", "news"],
  ["/notifications", "bell"],
];

function iconFor(href: string): IconName {
  if (href === "/") return "home";
  if (!href.startsWith("/")) return "link";
  return iconPrefixes.find(([prefix]) => href === prefix || href.startsWith(`${prefix}/`) || href.startsWith(`${prefix}?`))?.[1] ?? "link";
}

function isCurrent(href: string, path: string): boolean {
  if (!href.startsWith("/")) return false;
  const target = href.split(/[?#]/)[0]!;
  return target === "/" ? path === "/" : path === target || path.startsWith(`${target}/`);
}

/**
 * Global and organization navigation (spec 008 FR-012, FR-018). A disclosure menu: links are plain links, and an
 * entry with children has its own toggle button. Escape closes the open part and returns focus to its button; a click
 * outside or focus leaving the menu closes it. At narrow widths the same list, with the same labels, sits behind one
 * menu button.
 */
export function GlobalMenu({ links, label, id, icons = false }: { links: NavLink[]; label: string; id: string; icons?: boolean }) {
  const t = useTranslations("shell");
  const path = usePathname() ?? "/";
  const [open, setOpen] = useState<string | null>(null);
  const [expanded, setExpanded] = useState(false);
  const nav = useRef<HTMLElement>(null);
  const listId = useId();

  useEffect(() => {
    const close = (e: PointerEvent) => {
      if (nav.current && !nav.current.contains(e.target as Node)) {
        setOpen(null);
        setExpanded(false);
      }
    };
    document.addEventListener("pointerdown", close);
    return () => document.removeEventListener("pointerdown", close);
  }, []);

  function onKeyDown(e: KeyboardEvent<HTMLElement>) {
    if (e.key !== "Escape") {
      return;
    }
    if (open) {
      const toggle = nav.current?.querySelector<HTMLButtonElement>(`[data-toggle="${open}"]`);
      setOpen(null);
      toggle?.focus();
    } else if (expanded) {
      setExpanded(false);
      nav.current?.querySelector<HTMLButtonElement>(".shell-menu-button")?.focus();
    }
  }

  function onBlur(e: FocusEvent<HTMLElement>) {
    if (!nav.current?.contains(e.relatedTarget as Node | null)) {
      setOpen(null);
      setExpanded(false);
    }
  }

  if (links.length === 0) {
    return null;
  }
  return (
    <nav ref={nav} className="shell-menu" aria-label={label} id={id} onKeyDown={onKeyDown} onBlur={onBlur} data-expanded={expanded}>
      <button type="button" className="button button-quiet shell-menu-button" aria-expanded={expanded} aria-controls={listId} onClick={() => setExpanded((x) => !x)}>
        {t("menu")}
      </button>
      <ul className="shell-menu-list" id={listId}>
        {links.map((link) => {
          const submenu = `${listId}-${link.nodeId}`;
          return (
            <li key={link.nodeId} className="shell-menu-item">
              <a href={link.href} aria-current={isCurrent(link.href, path) ? "page" : undefined}>
                {icons ? <Icon name={iconFor(link.href)} size={22} /> : null}
                <bdi>{link.label}</bdi>
                {link.owner ? <span className="shell-owner">{link.owner}</span> : null}
              </a>
              {link.children.length > 0 ? (
                <>
                  <button
                    type="button"
                    className="button button-quiet shell-submenu-toggle"
                    data-toggle={link.nodeId}
                    aria-expanded={open === link.nodeId}
                    aria-controls={submenu}
                    aria-label={t("submenu", { label: link.label })}
                    onClick={() => setOpen((o) => (o === link.nodeId ? null : link.nodeId))}
                  >
                    <span aria-hidden="true" className="shell-chevron" />
                  </button>
                  <ul className="shell-submenu" id={submenu} hidden={open !== link.nodeId}>
                    {link.children.map((child) => (
                      <li key={child.nodeId}>
                        <a href={child.href}>
                          <bdi>{child.label}</bdi>
                          {child.owner ? <span className="shell-owner">{child.owner}</span> : null}
                        </a>
                      </li>
                    ))}
                  </ul>
                </>
              ) : null}
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
