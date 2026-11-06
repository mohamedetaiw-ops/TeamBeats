import { notFound } from "next/navigation";
import { createTranslator } from "next-intl";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { checkBrand, getBrand, getTokenSchema, listBrands } from "@/lib/brand/client";
import { BrandManager, type PreviewSample } from "@/features/brand/BrandManager";
import "@/features/brand/brand.css";

const sampleKeys = ["shell", "navHome", "navNews", "heading", "body", "secondary", "link", "primary", "secondaryAction", "success", "warning", "danger"] as const;

/** Preview text in a fixed language per direction, whatever the editor's own language is. */
async function sample(locale: "en" | "ar"): Promise<PreviewSample> {
  const messages = (await import(`@/messages/${locale}.json`)).default;
  const t = createTranslator({ locale, messages, namespace: "brand.sample" });
  return Object.fromEntries(sampleKeys.map((k) => [k, t(k)])) as PreviewSample;
}

/**
 * Brand manager (spec 033 T011): token groups, the contrast table, brand assets and a live preview in light and dark,
 * left to right and right to left. The section appears only when the API lists it, and every call is authorized again
 * by the API; the page itself decides nothing about access.
 */
export default async function BrandingPage({ searchParams }: { searchParams: Promise<{ brand?: string }> }) {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "branding")) {
    notFound();
  }
  const [schema, brands] = await Promise.all([getTokenSchema(), listBrands()]);
  if (!schema.ok || !brands.ok || brands.data.length === 0) {
    notFound();
  }
  const { brand: requested } = await searchParams;
  const chosen =
    brands.data.find((b) => b.id === requested) ?? brands.data.find((b) => b.assignedScopes.includes("tenant")) ?? brands.data[0]!;
  const brand = await getBrand(chosen.id);
  if (!brand.ok) {
    notFound();
  }
  const check = await checkBrand(brand.data);
  const t = await getTranslations("brand");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <BrandManager
        key={brand.data.id}
        schema={schema.data}
        brands={brands.data}
        brand={brand.data}
        initialPreview={check.ok ? check.data.preview : null}
        samples={{ ltr: await sample("en"), rtl: await sample("ar") }}
      />
    </>
  );
}
