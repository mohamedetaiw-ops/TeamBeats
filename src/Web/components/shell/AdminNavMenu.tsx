"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { usePathname } from "next/navigation";
import { Icon } from "@/components/ui/Icon";
import { currentSection } from "./AdminNavLink";

/**
 * On a phone the admin menu folds behind one button that names the section shown, so the page starts with its own
 * content instead of 36 menu items (spec 036 DLS-092). Wider screens always show the full menu; the button is hidden
 * there by CSS. Following a link closes the menu, also when the link is the section already shown, so Back never
 * brings an earlier page back with its menu open.
 */
export function AdminNavMenu({
  label,
  items,
  children,
}: {
  label: string;
  items: readonly { path: string; label: string }[];
  children: ReactNode;
}) {
  const pathname = usePathname() ?? "";
  const [openOn, setOpenOn] = useState<string | null>(null);
  const open = openOn === pathname;
  const current = currentSection(
    pathname,
    items.map((i) => i.path),
  );
  // Following any menu link closes the menu, the current section's link too (the path does not change then).
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const node = list.current;
    if (!node) return;
    const close = (e: MouseEvent) => {
      if ((e.target as HTMLElement | null)?.closest("a")) setOpenOn(null);
    };
    node.addEventListener("click", close);
    return () => node.removeEventListener("click", close);
  }, []);
  const name = items.find((i) => i.path === current)?.label;
  return (
    <>
      <button
        type="button"
        className="admin-nav-toggle"
        aria-expanded={open}
        aria-controls="admin-nav-list"
        onClick={() => setOpenOn(open ? null : pathname)}
      >
        <span className="admin-nav-toggle-label">{label}</span>
        {name ? <strong>{name}</strong> : null}
        <Icon name="chevron-down" />
      </button>
      <div
        ref={list}
        id="admin-nav-list"
        className="admin-nav-list"
        data-open={open ? "" : undefined}
      >
        {children}
      </div>
    </>
  );
}
