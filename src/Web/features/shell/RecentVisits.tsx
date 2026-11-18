"use client";

import { useEffect } from "react";
import { usePathname } from "next/navigation";

/**
 * Records each page the person opens as a recent visit (spec 008 FR-017): the path alone, never the query string or
 * fragment, with the page title as its label. The API keeps the last 30 and drops ones the person can no longer open.
 * It renders nothing; a shared kiosk never mounts it, so nothing is kept for a shared device.
 */
export function RecentVisits() {
  const path = usePathname();

  useEffect(() => {
    if (!path || path === "/") return;
    // The title is set after the page renders, so wait a moment before reading it.
    const timer = setTimeout(() => {
      const label = (document.title || path).trim().slice(0, 200) || path;
      void fetch("/api/v1/me/recents", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ path, label }),
        keepalive: true,
      }).catch(() => undefined);
    }, 800);
    return () => clearTimeout(timer);
  }, [path]);

  return null;
}
