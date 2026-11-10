import type { ThemeVariant } from "./types";

/** The design keys and navigation styles the token schema allows (spec 036). Anything else falls back to the default. */
const presets = ["beats", "atlas", "pulse", "studio"] as const;
const navigations = ["rail", "top", "sidebar"] as const;

export type DesignKey = (typeof presets)[number];
export type NavigationStyle = (typeof navigations)[number];

/**
 * Reads the effective design from a resolved theme. The API already resolved and validated both values; this only
 * narrows them to the known keys so a page never receives an attribute it has no styles for.
 */
export function designOf(variants: ThemeVariant[]): { preset: DesignKey; navigation: NavigationStyle } {
  const tokens = variants[0]?.tokens ?? {};
  const preset = presets.find((p) => p === tokens["design.preset"]) ?? "beats";
  const navigation = navigations.find((n) => n === tokens["layout.navigation"]) ?? "rail";
  return { preset, navigation };
}
