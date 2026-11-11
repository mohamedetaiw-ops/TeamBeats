import type { Metadata } from "next";
import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import { direction, isLocale } from "@/i18n/locales";
import { getTheme } from "@/lib/experience/client";
import { ThemeLinks } from "@/components/shell/ThemeLinks";
import { getPreferences } from "@/lib/shell/client";
// Default brand fonts (spec 033, ADR-0019): self-hosted, one file per script subset, fetched only when a page uses
// characters of that script.
import "@fontsource/readex-pro/400.css";
import "@fontsource/readex-pro/500.css";
import "@fontsource/readex-pro/600.css";
import "@fontsource/readex-pro/700.css";
import "@fontsource/alexandria/600.css";
import "@fontsource/alexandria/700.css";
// Fonts of the Atlas, Pulse and Studio designs (spec 036, ADR-0021). Only the @font-face rules ship with every page; a browser
// downloads a font file only when the active design uses that family for characters on the page.
import "@fontsource/ibm-plex-sans/400.css";
import "@fontsource/ibm-plex-sans/600.css";
import "@fontsource/ibm-plex-sans-arabic/400.css";
import "@fontsource/ibm-plex-sans-arabic/600.css";
import "@fontsource/cairo/400.css";
import "@fontsource/cairo/700.css";
// Studio: Inter for Latin text; its Arabic is IBM Plex Sans Arabic, already loaded above.
import "@fontsource/inter/400.css";
import "@fontsource/inter/500.css";
import "@fontsource/inter/600.css";
import "./tokens.css";
import "./globals.css";
import "@/components/ui/ui.css";
import "@/components/ui/controls.css";
import "@/components/ui/overlays.css";
import "@/components/ui/inputs.css";
import "@/components/ui/data.css";
import "@/components/ui/assistant.css";
import "@/components/ui/charts.css";
import "@/components/ui/patterns.css";
import "@/components/ui/calendar.css";
import "@/components/ui/designs.css";
import { MessagesProvider } from "@/components/ui/Messages";
import { designOf } from "@/lib/experience/design";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("app");
  return { title: t("name") };
}

export default async function RootLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  const t = await getTranslations("app");
  const dir = isLocale(locale) ? direction(locale) : "ltr";
  // The tenant theme is awaited here, before the page renders, so a page that links the theme of its own scope
  // always comes later in the head and wins. Signed-out pages get no theme and keep the platform tokens.
  const [theme, prefs] = await Promise.all([getTheme("tenant"), getPreferences()]);
  const density = prefs.density === "compact" ? "compact" : "comfortable";
  const mode = prefs.mode === "light" || prefs.mode === "dark" ? prefs.mode : "system";
  // Text size scales the root font, so every rem-based size follows (spec 008 preferences, WCAG 1.4.4).
  // The design (spec 036) picks the structural variant of the shell; the rest of the design arrives as tokens.
  const design = designOf(theme.ok ? theme.data.variants : []);
  const scale = ["125", "150", "200"].includes(prefs.text_scale) ? `${prefs.text_scale}%` : undefined;
  return (
    <html lang={locale} dir={dir} data-design={design.preset} data-nav={design.navigation} data-mode={mode} data-density={density} data-motion={prefs.motion} style={scale ? { fontSize: scale } : undefined}>
      <body>
        {theme.ok ? <ThemeLinks variants={theme.data.variants} density={density} mode={mode} /> : null}
        <a className="skip-link" href="#main">
          {t("skipToContent")}
        </a>
        <NextIntlClientProvider>
          <MessagesProvider>{children}</MessagesProvider>
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
