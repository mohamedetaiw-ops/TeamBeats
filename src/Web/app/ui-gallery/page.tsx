import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getTheme } from "@/lib/experience/client";
import { ThemeLinks } from "@/components/shell/ThemeLinks";
import { Gallery } from "./Gallery";
import { DesignSystem } from "./DesignSystem";

/**
 * Development gallery of the shared states and interaction primitives (spec 008 T001 to T003), used by the Playwright
 * suite. It exists only where the development fixtures are on, like the reference component packs.
 * `?density=compact` loads the compact theme of the signed-in tenant.
 */
export default async function UiGallery({ searchParams }: { searchParams: Promise<{ density?: string }> }) {
  if (process.env.NEXT_PUBLIC_TB_REFERENCE_PACKS !== "true") {
    notFound();
  }
  const { density } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  const compact = density === "compact" && me.ok ? await getTheme("tenant") : null;
  const t = await getTranslations("gallery");
  return (
    <main id="main" className="page" data-density={density === "compact" ? "compact" : "comfortable"}>
      {compact?.ok ? <ThemeLinks variants={compact.data.variants} density="compact" /> : null}
      <h1>{t("title")}</h1>
      <Gallery />
      <h1>{t("design_system")}</h1>
      <DesignSystem />
    </main>
  );
}
