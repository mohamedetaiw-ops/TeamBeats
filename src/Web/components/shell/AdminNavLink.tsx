"use client";

import type { ReactNode } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";

/**
 * One admin menu link. The item of the page shown, and of its sub pages, is marked as the current page (spec 036
 * DLS-092, spec 006 FR-020). `paths` are every section path, so a section whose path starts another one's (none today)
 * still resolves to the longest match.
 */
export function AdminNavLink({ href, paths, children }: { href: string; paths: readonly string[]; children: ReactNode }) {
  const pathname = usePathname() ?? "";
  const current = currentSection(pathname, paths) === href;
  return (
    <Link href={href} aria-current={current ? "page" : undefined} data-current={current ? "" : undefined}>
      {children}
    </Link>
  );
}

/** The section path that owns a page path: the longest section path equal to it or ending at a `/` before it. */
export function currentSection(pathname: string, paths: readonly string[]): string | undefined {
  return paths
    .filter((p) => pathname === p || pathname.startsWith(`${p}/`))
    .sort((a, b) => b.length - a.length)[0];
}
