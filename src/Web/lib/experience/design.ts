import type { ThemeVariant } from "./types";

/** The design keys and navigation styles the token schema allows (spec 036). Anything else falls back to the default. */
const presets = ["beats", "atlas", "pulse", "studio"] as const;
const navigations = ["rail", "top", "sidebar"] as const;

/**
 * The design descriptor (spec 036 DLS-110): choice tokens that pick the presentation variant of a fixed list of shared
 * parts. The first value of each list is the platform default. A variant changes presentation only.
 */
const descriptor = {
  density: { token: "density.default", values: ["comfortable", "compact"] },
  imagery: { token: "imagery.style", values: ["balanced", "image_led", "typographic"] },
  news: { token: "variant.news_lead", values: ["editorial", "compact", "typographic"] },
  stats: { token: "variant.stat_tiles", values: ["tinted", "outlined", "plain"] },
  rows: { token: "variant.work_rows", values: ["comfortable", "dense"] },
  tabs: { token: "variant.section_tabs", values: ["underline", "segmented"] },
  cards: { token: "variant.page_cards", values: ["raised", "outlined", "flat"] },
  briefing: { token: "variant.briefing", values: ["night", "light"] },
} as const;

export type DesignKey = (typeof presets)[number];
export type NavigationStyle = (typeof navigations)[number];
export type DesignDescriptor = { [K in keyof typeof descriptor]: (typeof descriptor)[K]["values"][number] };
export type Design = { preset: DesignKey; navigation: NavigationStyle } & DesignDescriptor;

/**
 * Reads the effective design from a resolved theme. The API already resolved and validated every value; this only
 * narrows them to the known keys so a page never receives an attribute it has no styles for.
 */
export function designOf(variants: ThemeVariant[]): Design {
  const tokens = variants[0]?.tokens ?? {};
  const preset = presets.find((p) => p === tokens["design.preset"]) ?? "beats";
  const navigation = navigations.find((n) => n === tokens["layout.navigation"]) ?? "rail";
  const pick = <K extends keyof typeof descriptor>(key: K): DesignDescriptor[K] => {
    const { token, values } = descriptor[key];
    return ((values as readonly string[]).find((v) => v === tokens[token]) ?? values[0]) as DesignDescriptor[K];
  };
  return {
    preset,
    navigation,
    density: pick("density"),
    imagery: pick("imagery"),
    news: pick("news"),
    stats: pick("stats"),
    rows: pick("rows"),
    tabs: pick("tabs"),
    cards: pick("cards"),
    briefing: pick("briefing"),
  };
}

/** The descriptor as attributes of the document element, which the variant styles read (components/ui/variants.css). */
export function designAttributes(design: Design): Record<string, string> {
  return {
    "data-design": design.preset,
    "data-nav": design.navigation,
    "data-imagery": design.imagery,
    "data-v-news": design.news,
    "data-v-stats": design.stats,
    "data-v-rows": design.rows,
    "data-v-tabs": design.tabs,
    "data-v-cards": design.cards,
    "data-v-briefing": design.briefing,
  };
}
