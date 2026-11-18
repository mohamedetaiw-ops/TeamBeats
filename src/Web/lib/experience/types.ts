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
  /** The component's own content budget at this width when it is lower than the platform's (spec 032 FR-071). */
  budget?: number | null;
  /** The widths, in grid columns, this viewer may choose for it in personalization mode (spec 008 FR-025). */
  widths?: number[] | null;
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
  /** Placements the server left out because an earlier one already shows their data source and view (spec 032 FR-072). */
  dropped?: { id: string; componentRef: string; region: string; keptPlacement: string; reasonKey: string }[] | null;
};

export type ThemeVariant = {
  mode: "light" | "dark";
  density: "comfortable" | "compact";
  cssUrl: string;
  /** Resolved token values by token name, including the design choices `design.preset` and `layout.navigation`. */
  tokens?: Record<string, string>;
};

export type EffectiveTheme = { scope: string; variants: ThemeVariant[] };
