"use client";

import { useEffect } from "react";
import { flushTelemetry, queueTelemetry, setTelemetryPage, type PageKey } from "@/lib/experience/telemetry";
import { collectVitals, deviceClass } from "@/lib/experience/web-vitals";

/**
 * Reports the page's Core Web Vitals once, when the viewer leaves or hides it, and names the page for placement failure
 * reports (spec 032 section 13). It renders nothing.
 */
export function PageTelemetry({ page }: { page: PageKey }) {
  const { pageId, release, template, layout } = page;
  useEffect(() => {
    setTelemetryPage({ pageId, release, template, layout });
    const stop = collectVitals();
    let sent = false;
    const send = () => {
      if (sent || document.visibilityState !== "hidden") return;
      sent = true;
      const vitals = stop();
      const device = deviceClass(window.innerWidth);
      queueTelemetry({ name: "page.web_vitals", pageId, template, layout, deviceClass: device, ...vitals });
      flushTelemetry();
      // The same vitals as essential analytics (spec 021 FR-020) for the performance dashboard; no person is attached.
      const fields: Record<string, string | number> = { pageId, release, deviceClass: device };
      const theme = document.documentElement.dataset.design;
      if (theme) fields.theme = theme;
      for (const key of ["lcp", "inp", "cls"] as const) {
        const value = vitals[key];
        if (value !== null) fields[key] = value;
      }
      const body = JSON.stringify({ events: [{ name: "page.vitals", fields }] });
      void fetch("/api/v1/analytics/events", { method: "POST", headers: { "content-type": "application/json" }, body, keepalive: true }).catch(() => undefined);
    };
    document.addEventListener("visibilitychange", send);
    return () => {
      document.removeEventListener("visibilitychange", send);
      setTelemetryPage(null);
    };
  }, [pageId, release, template, layout]);
  return null;
}
