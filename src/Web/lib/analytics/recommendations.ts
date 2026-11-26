import type { RecommendationSurface } from "./types";

/**
 * Reports that the person opened something ranked or suggested for them (spec 021 R3, FR-024). The server adds the
 * tenant and the pseudonymous key; a failure is ignored, because analytics never gets in the way.
 */
export function reportOpened(surface: RecommendationSurface): void {
  const body = JSON.stringify({ events: [{ name: "recommendation.outcome", fields: { surface, outcome: "opened" } }] });
  fetch("/api/v1/analytics/events", { method: "POST", headers: { "content-type": "application/json" }, body, keepalive: true }).catch(() => undefined);
}
