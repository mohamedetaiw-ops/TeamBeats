import type { ThemeVariant } from "@/lib/experience/types";

/**
 * Links the compiled theme of a scope (spec 033 T010). The stylesheets are render-blocking and hoisted to the head,
 * so the page paints once with the final tokens and switching scope causes no layout shift. With the mode on system,
 * the light variant applies by default and the dark one when the device prefers it; a personal mode choice (spec 008)
 * links only that variant. Density follows the personal choice.
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
        href={v.cssUrl}
        media={mode === "system" && v.mode === "dark" ? "(prefers-color-scheme: dark)" : "all"}
        precedence="theme"
      />
    ));
}
