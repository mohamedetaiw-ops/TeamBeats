import { notFound } from "next/navigation";
import { createTranslator } from "next-intl";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getBrandPreview, listBrands } from "@/lib/brand/client";
import { listUnits } from "@/lib/experience/client";
import type { PreviewSample } from "@/features/brand/BrandPreview";
import { ThemePreviewer } from "@/features/brand/ThemePreviewer";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { ButtonLink } from "@/components/ui/Button";
import "@/features/brand/brand.css";

const sampleKeys = ["shell", "navHome", "navNews", "heading", "body", "secondary", "link", "primary", "secondaryAction", "success", "warning", "danger"] as const;

async function sample(locale: "en" | "ar"): Promise<PreviewSample> {
  const messages = (await import(`@/messages/${locale}.json`)).default;
  const t = createTranslator({ locale, messages, namespace: "brand.sample" });
  return Object.fromEntries(sampleKeys.map((k) => [k, t(k)])) as PreviewSample;
}

/**
 * Theme preview (spec 033 R2 T016, FR-008, FR-023): a brand on a sample shell, page and components, as a chosen
 * organization sees it, in light and dark, both languages and three device widths, with the checks of that result. The
 * API resolves every value; the page only lays the sample out.
 */
export default async function BrandPreviewPage({ searchParams }: { searchParams: Promise<{ brand?: string }> }) {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "branding")) {
    notFound();
  }
  const brands = await listBrands();
  if (!brands.ok || brands.data.length === 0) {
    notFound();
  }
  const { brand: requested } = await searchParams;
  const chosen = brands.data.find((b) => b.id === requested) ?? brands.data.find((b) => b.assignedScopes.includes("tenant")) ?? brands.data[0]!;
  const [preview, units] = await Promise.all([getBrandPreview(chosen.id), listUnits()]);
  if (!preview.ok) {
    notFound();
  }
  const t = await getTranslations("brand.previewPage");
  return (
    <>
      <AdminHeader
        section="branding"
        title={t("title", { brand: chosen.name })}
        description={t("intro")}
        actions={
          <ButtonLink href={`/admin/branding?brand=${encodeURIComponent(chosen.id)}`} variant="secondary" icon="chevron-back">
            {t("back")}
          </ButtonLink>
        }
      />
      <ThemePreviewer
        brandId={chosen.id}
        initial={preview.data}
        units={units.ok ? units.data.filter((u) => u.status === "active") : []}
        samples={{ en: await sample("en"), ar: await sample("ar") }}
      />
    </>
  );
}
