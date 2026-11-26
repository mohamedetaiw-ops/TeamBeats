"use client";

import { useEffect } from "react";
import { reportOpened } from "@/lib/analytics/recommendations";

/**
 * Reports the first click on a result of this page to the query log (spec 012 FR-013), so the search dashboard of spec
 * 021 can show click-through and queries nobody clicks. It renders nothing and never holds up the link: the report is
 * sent with keepalive while the browser follows it, and a failure is ignored. Opening a result ranked from the person's
 * use is also an outcome for the usefulness report of spec 021 R3.
 */
export function SearchClicks({ queryId }: { queryId: string }) {
  useEffect(() => {
    let sent = false;
    function onClick(event: MouseEvent) {
      const target = event.target instanceof Element ? event.target : null;
      const link = target?.closest(".search-results a");
      const position = Number(link?.closest("li[data-position]")?.getAttribute("data-position"));
      if (!link) return;
      if (link.closest("li[data-personalized]")) reportOpened("search");
      if (sent || !Number.isInteger(position) || position < 1) return;
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
