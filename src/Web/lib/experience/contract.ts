import type { JsonValue } from "./types";

/**
 * What a placement component receives (spec 032 FR-025 to FR-028). Components never import each other: they get their
 * own properties, the context keys their manifest declares, and an event channel limited to their declared events.
 */
export type PlacementEvents = {
  publish: (event: string, payload: Record<string, JsonValue>) => void;
  subscribe: (event: string, handler: (payload: Record<string, JsonValue>) => void) => () => void;
};

export type PlacementProps = {
  placementId: string;
  props: Record<string, JsonValue>;
  context: Record<string, string>;
  events: PlacementEvents;
};

/** Localized plain text (`text` property type): the value for the locale, then English, then any value. */
export function localized(value: JsonValue | undefined, locale: string | undefined): string {
  if (typeof value === "string") {
    return value;
  }
  if (value && typeof value === "object" && !Array.isArray(value)) {
    const byLocale = value as Record<string, JsonValue>;
    const pick = (locale && byLocale[locale]) ?? byLocale.en ?? Object.values(byLocale)[0];
    return typeof pick === "string" ? pick : "";
  }
  return "";
}
