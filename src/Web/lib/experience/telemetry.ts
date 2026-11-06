/**
 * Placement failure signal (spec 032 FR-066). It carries the component, the placement and the kind of failure, never
 * page content or user data. The sink that ships it to the API arrives with T021; until then it is a DOM event.
 */
export type PlacementFailure = { componentRef: string; placementId: string; kind: "error" | "missing" | "undeclared_event" | "invalid_payload" };

export function reportPlacementFailure(failure: PlacementFailure): void {
  if (typeof window !== "undefined") {
    window.dispatchEvent(new CustomEvent("tb:placement-failure", { detail: failure }));
  }
}
