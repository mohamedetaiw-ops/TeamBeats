import { themeMode } from "@/lib/shell/types";
import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getTheme, previewPage, previewSubmission } from "@/lib/experience/client";
import { getPreferences } from "@/lib/shell/client";
import { direction, isLocale } from "@/i18n/locales";
import { ThemeLinks } from "@/components/shell/ThemeLinks";
import { PageRenderer } from "@/components/experience/PageRenderer";

/**
 * The saved draft as employees would see it, for the builder's previews by device and direction (spec 032 T018). The
 * builder shows it in a frame at a device width; the language decides the text and the direction. Only the page's
 * editors get it: the API answers not found to anyone else. With `approval=1` it is the submission waiting for
 * approval instead, for its approver (spec 032 R2 T023).
 */
export default async function DraftPreview({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ locale?: string; approval?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { id } = await params;
  const { locale, approval } = await searchParams;
  const shown = locale && isLocale(locale) && me.data.tenant.enabledLocales.includes(locale) ? locale : me.data.locale;
  const page = approval === "1" ? await previewSubmission(id, shown) : await previewPage(id, shown);
  if (!page.ok) {
    notFound();
  }
  const scoped = page.data.themeScope === "tenant" ? null : await getTheme(page.data.themeScope);
  const prefs = await getPreferences();
  const t = await getTranslations("experience");
  return (
    <>
      {scoped?.ok ? (
        <ThemeLinks variants={scoped.data.variants} density={prefs.density === "compact" ? "compact" : "comfortable"} mode={themeMode(prefs.mode)} />
      ) : null}
      <main id="main" lang={shown} dir={isLocale(shown) ? direction(shown) : "ltr"}>
        <PageRenderer page={page.data} tabsLabel={t("panels")} tabLabel={(n) => t("panel", { number: n })} />
      </main>
    </>
  );
}
