"use client";

import { useState, type ReactNode } from "react";
import { usePathname } from "next/navigation";
import { Icon } from "@/components/ui/Icon";
import { currentSection } from "./AdminNavLink";

/**
 * On a phone the admin menu folds behind one button that names the section shown, so the page starts with its own
 * content instead of 36 menu items (spec 036 DLS-092). Wider screens always show the full menu; the button is hidden
 * there by CSS. The menu closes again after a link is followed, because it is open only for the path it was opened on.
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
        id="admin-nav-list"
        className="admin-nav-list"
        data-open={open ? "" : undefined}
      >
        {children}
      </div>
    </>
  );
}
