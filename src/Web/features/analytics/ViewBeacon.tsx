"use client";

import { useEffect } from "react";
import type { AnalyticsSource } from "@/lib/analytics/types";

/**
 * Reports that the person opened an item (spec 021 FR-003, <c>content.viewed</c>). The server adds the tenant, the
 * pseudonymous key and the unit; a failure is ignored, because analytics never gets in the way of reading.
 */
export function ViewBeacon({ source, itemId, contentType, siteId }: { source: AnalyticsSource; itemId: string; contentType?: string; siteId?: string }) {
  useEffect(() => {
    // The content type and site feed the workspace filters (spec 021 FR-006) when the page knows them.
    const body = JSON.stringify({ events: [{ name: "content.viewed", fields: { source, itemId, contentType, siteId } }] });
    fetch("/api/v1/analytics/events", { method: "POST", headers: { "content-type": "application/json" }, body, keepalive: true }).catch(() => undefined);
  }, [source, itemId, contentType, siteId]);
  return null;
}
