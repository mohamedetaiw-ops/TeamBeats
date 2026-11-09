"use client";

import { useEffect } from "react";
import type { AnalyticsSource } from "@/lib/analytics/types";

/**
 * Reports that the person opened an item (spec 021 FR-003, <c>content.viewed</c>). The server adds the tenant, the
 * pseudonymous key and the unit; a failure is ignored, because analytics never gets in the way of reading.
 */
export function ViewBeacon({ source, itemId }: { source: AnalyticsSource; itemId: string }) {
  useEffect(() => {
    const body = JSON.stringify({ events: [{ name: "content.viewed", fields: { source, itemId } }] });
    fetch("/api/v1/analytics/events", { method: "POST", headers: { "content-type": "application/json" }, body, keepalive: true }).catch(() => undefined);
  }, [source, itemId]);
  return null;
}
