import type { ThemeVariant } from "@/lib/experience/types";

/**
 * Links the compiled theme of a scope (spec 033 T010). The stylesheets are render-blocking and hoisted to the head,
 * so the page paints once with the final tokens and switching scope causes no layout shift. The light variant
 * applies by default and the dark one when the device prefers it. Density stays comfortable until the personal
 * choice of spec 008 lands.
 */
export function ThemeLinks({ variants, density = "comfortable" }: { variants: ThemeVariant[]; density?: ThemeVariant["density"] }) {
  return variants
    .filter((v) => v.density === density)
    .map((v) => (
      <link
        key={v.mode}
        rel="stylesheet"
        href={v.cssUrl}
        media={v.mode === "dark" ? "(prefers-color-scheme: dark)" : "all"}
        precedence="theme"
      />
    ));
}
