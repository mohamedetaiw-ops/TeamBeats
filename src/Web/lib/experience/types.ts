/**
 * Shapes of the spec 032 render API (GET /api/v1/experience/render) and the spec 033 theme API. The API decides
 * everything here: which placements exist, which are unavailable, which context keys and events each one gets. The
 * renderer only draws what it receives (constitution XXX).
 */
export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export type RenderedPlacement = {
  id: string;
  componentRef: string;
  state: "ready" | "unavailable";
  span: number;
  props: Record<string, JsonValue>;
  context: Record<string, string>;
  publishes: string[];
  subscribes: string[];
  /** Added by this viewer in personalization mode (spec 008); visible to them only. */
  personal: boolean;
};

export type SectionMode = "contained" | "full_bleed" | "fixed" | "fluid";

/** `rights` lists what this viewer may change here in personalization mode (spec 008); empty or null when nothing. */
export type RenderedRegion = { id: string; span: number; section: SectionMode; placements: RenderedPlacement[]; rights?: PersonalRight[] | null };

export type PersonalRight = "add" | "hide" | "remove" | "reorder";

export type RenderedPage = {
  pageId: string;
  release: number;
  template: string;
  layout: string;
  variant: "spacious" | "balanced" | "compact";
  fallback: "stack" | "two_then_stack" | "tabs";
  regions: RenderedRegion[];
  themeScope: string;
};

export type ThemeVariant = {
  mode: "light" | "dark";
  density: "comfortable" | "compact";
  cssUrl: string;
  /** Resolved token values by token name, including the design choices `design.preset` and `layout.navigation`. */
  tokens?: Record<string, string>;
};

export type EffectiveTheme = { scope: string; variants: ThemeVariant[] };
