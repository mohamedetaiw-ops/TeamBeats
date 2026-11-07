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
      queueTelemetry({ name: "page.web_vitals", pageId, template, layout, deviceClass: deviceClass(window.innerWidth), ...vitals });
      flushTelemetry();
    };
    document.addEventListener("visibilitychange", send);
    return () => {
      document.removeEventListener("visibilitychange", send);
      setTelemetryPage(null);
    };
  }, [pageId, release, template, layout]);
  return null;
}
