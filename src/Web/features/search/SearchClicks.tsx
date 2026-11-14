"use client";

import { useEffect } from "react";

/**
 * Reports the first click on a result of this page to the query log (spec 012 FR-013), so the search dashboard of spec
 * 021 can show click-through and queries nobody clicks. It renders nothing and never holds up the link: the report is
 * sent with keepalive while the browser follows it, and a failure is ignored.
 */
export function SearchClicks({ queryId }: { queryId: string }) {
  useEffect(() => {
    let sent = false;
    function onClick(event: MouseEvent) {
      const target = event.target instanceof Element ? event.target : null;
      const link = target?.closest(".search-results a");
      const position = Number(link?.closest("li[data-position]")?.getAttribute("data-position"));
      if (sent || !link || !Number.isInteger(position) || position < 1) return;
      sent = true;
      void fetch("/api/v1/search/clicks", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ queryId, position }),
        keepalive: true,
      }).catch(() => undefined);
    }
    document.addEventListener("click", onClick, true);
    return () => document.removeEventListener("click", onClick, true);
  }, [queryId]);
  return null;
}
