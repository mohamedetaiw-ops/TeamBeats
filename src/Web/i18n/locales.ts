export const locales = ["ar", "en", "fr", "ur", "hi"] as const;
export type Locale = (typeof locales)[number];

/** Launch-complete locales (R1 decision 2026-11-04). The others fall back to English until R2. */
export const launchLocales: readonly Locale[] = ["ar", "en"];
export const fallbackLocale: Locale = "en";

const rtl = new Set<Locale>(["ar", "ur"]);

export function isLocale(value: unknown): value is Locale {
  return typeof value === "string" && (locales as readonly string[]).includes(value);
}

export function direction(locale: Locale): "rtl" | "ltr" {
  return rtl.has(locale) ? "rtl" : "ltr";
}
