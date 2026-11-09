/**
 * Frontend error reports (spec 021 US9 scenario 6, FR-020). A report names the release, the kind of failure, the
 * component reference and version and the area of the app, and the correlation ID when there is one. The message, the
 * stack and any payload are never sent, so nothing a person typed or saw can leak. Errors are essential telemetry and
 * are kept when the tenant turns nonessential analytics off, without a user key.
 */
export type UiErrorKind = "render" | "boundary" | "network" | "chunk";

export type UiError = { kind: UiErrorKind; component?: string | null; area?: string | null; correlationId?: string | null };

const KEY = /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/;
const COMPONENT = /^[a-z][a-z0-9]*(\.[a-z][a-z0-9_]*)+@\d{1,3}$/;

export const release = (): string => {
  const value = process.env.NEXT_PUBLIC_TEAMBEATS_RELEASE ?? "dev";
  return KEY.test(value) ? value : "dev";
};

/** The first part of the path, as the area of the app (`content`, `admin`), never the rest, which may hold IDs. */
export function areaOf(pathname: string): string {
  const first = pathname.split("/").filter(Boolean)[0] ?? "home";
  return KEY.test(first) ? first : "other";
}

/** A Next.js chunk that failed to load reads as its own kind, so a bad deployment stands out from a bug. */
export function kindOf(error: unknown, fallback: UiErrorKind): UiErrorKind {
  const name = error instanceof Error ? error.name : "";
  return name === "ChunkLoadError" ? "chunk" : fallback;
}

export function reportUiError(error: UiError): void {
  if (typeof window === "undefined") return;
  const fields: Record<string, string> = { kind: error.kind, release: release() };
  if (error.component && COMPONENT.test(error.component)) fields.component = error.component;
  if (error.area && KEY.test(error.area)) fields.area = error.area;
  if (error.correlationId && KEY.test(error.correlationId)) fields.correlationId = error.correlationId;
  const body = JSON.stringify({ events: [{ name: "ui.error", fields }] });
  // A lost report is never shown or retried: reporting must not cause another failure.
  void fetch("/api/v1/analytics/events", { method: "POST", headers: { "content-type": "application/json" }, body, keepalive: true }).catch(() => undefined);
}
