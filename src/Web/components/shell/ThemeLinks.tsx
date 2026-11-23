import type { ThemeVariant } from "@/lib/experience/types";

/**
 * Links the compiled theme of a scope (spec 033 T010). The stylesheets are render-blocking and hoisted to the head,
 * so the page paints once with the final tokens and switching scope causes no layout shift. With the mode on system,
 * the light variant applies by default, the dark one when the device prefers it, and high contrast (spec 033 R2 T017)
 * when the device asks for more contrast or forces its colors; it comes last so it wins. A personal mode choice
 * (spec 008) links only that variant. Density follows the personal choice. React never removes or changes a hoisted
 * stylesheet, so each choice links its own address: a new choice then adds fresh links after the old ones, and they win.
 */
export function ThemeLinks({
  variants,
  density = "comfortable",
  mode = "system",
}: {
  variants: ThemeVariant[];
  density?: ThemeVariant["density"];
  mode?: "system" | ThemeVariant["mode"];
}) {
  return variants
    .filter((v) => v.density === density && (mode === "system" || v.mode === mode))
    .map((v) => (
      <link
        key={v.mode}
        rel="stylesheet"
        href={`${v.cssUrl}&for=${mode}`}
        media={mode !== "system" ? "all" : v.mode === "dark" ? "(prefers-color-scheme: dark)" : v.mode === "high_contrast" ? "(prefers-contrast: more), (forced-colors: active)" : "all"}
        precedence="theme"
      />
    ));
}
