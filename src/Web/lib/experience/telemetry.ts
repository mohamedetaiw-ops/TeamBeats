import { reportUiError } from "@/lib/analytics/errors";

/**
 * Renderer telemetry (spec 032 section 13, FR-058, FR-066). Events carry identifiers, kinds and numbers only, never page
 * content or user data, and the API drops anything outside that shape. They are queued and sent in small batches with
 * `sendBeacon` when the page is hidden, so telemetry never delays rendering. Each failure is also a DOM event, which the
 * renderer tests listen to.
 */
export type PlacementFailure = { componentRef: string; placementId: string; kind: "error" | "missing" | "undeclared_event" | "invalid_payload" };

export type PageKey = { pageId: string; release: number; template: string; layout: string };

type TelemetryEvent = Record<string, string | number | null>;

const ENDPOINT = "/api/v1/experience/telemetry";
const MAX_BATCH = 20;
let queue: TelemetryEvent[] = [];
let current: PageKey | null = null;
let listening = false;

/** Sends what is queued now. */
export function flushTelemetry(): void {
  flush();
}

function flush(): void {
  if (queue.length === 0 || typeof navigator === "undefined") return;
  const events = queue.slice(0, MAX_BATCH);
  queue = queue.slice(MAX_BATCH);
  const body = new Blob([JSON.stringify({ events })], { type: "application/json" });
  // A lost batch only loses telemetry, so a failure here is never shown or retried.
  if (!navigator.sendBeacon?.(ENDPOINT, body)) {
    void fetch(ENDPOINT, { method: "POST", body, keepalive: true, headers: { "content-type": "application/json" } }).catch(() => undefined);
  }
  if (queue.length > 0) flush();
}

function listen(): void {
  if (listening || typeof window === "undefined") return;
  listening = true;
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") flush();
  });
  window.addEventListener("pagehide", flush);
}

/** Queues an event; sends a full batch at once and the rest when the page is hidden. */
export function queueTelemetry(event: TelemetryEvent): void {
  listen();
  queue.push(event);
  if (queue.length >= MAX_BATCH) flush();
}

/** The page whose placements are reporting, so failures carry its page key and release. */
export function setTelemetryPage(page: PageKey | null): void {
  listen();
  current = page;
}

export function reportPlacementFailure(failure: PlacementFailure): void {
  if (typeof window === "undefined") return;
  window.dispatchEvent(new CustomEvent("tb:placement-failure", { detail: failure }));
  queueTelemetry({
    name: "placement.render_failed",
    pageId: current?.pageId ?? null,
    release: current?.release ?? null,
    componentRef: failure.componentRef,
    placementId: failure.placementId,
    kind: failure.kind,
  });
  // A placement that throws is also a frontend error for support, with its component reference and version (spec 021 US9).
  if (failure.kind === "error") reportUiError({ kind: "render", component: failure.componentRef, area: "pages" });
}
