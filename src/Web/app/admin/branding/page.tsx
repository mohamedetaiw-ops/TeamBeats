import { notFound } from "next/navigation";
import { createTranslator } from "next-intl";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import { checkBrand, getBrand, getBrandApproval, getBrandDraft, getBrandReleases, getBrandSchedule, getTokenSchema, listBrands } from "@/lib/brand/client";
import { BrandManager, type PreviewSample } from "@/features/brand/BrandManager";
import { ThemeLifecycle } from "@/features/brand/ThemeLifecycle";
import "@/features/brand/brand.css";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { ButtonLink } from "@/components/ui/Button";

const sampleKeys = ["shell", "navHome", "navNews", "heading", "body", "secondary", "link", "primary", "secondaryAction", "success", "warning", "danger"] as const;

/** Preview text in a fixed language per direction, whatever the editor's own language is. */
async function sample(locale: "en" | "ar"): Promise<PreviewSample> {
  const messages = (await import(`@/messages/${locale}.json`)).default;
  const t = createTranslator({ locale, messages, namespace: "brand.sample" });
  return Object.fromEntries(sampleKeys.map((k) => [k, t(k)])) as PreviewSample;
}

/**
 * Brand manager (spec 033 T011) and publishing (R2 T014: draft, approval, schedule, rollback): token groups, the contrast table, brand assets and a live preview in light and dark,
 * left to right and right to left. The section appears only when the API lists it, and every call is authorized again
 * by the API; the page itself decides nothing about access.
 */
export default async function BrandingPage({ searchParams }: { searchParams: Promise<{ brand?: string }> }) {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "branding")) {
    notFound();
  }
  const [schema, brands, me] = await Promise.all([getTokenSchema(), listBrands(), apiGet<Me>("/api/v1/me")]);
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
  const [check, draft, releases, schedule, approval] = await Promise.all([
    checkBrand(brand.data),
    getBrandDraft(chosen.id),
    getBrandReleases(chosen.id),
    getBrandSchedule(chosen.id),
    getBrandApproval(chosen.id),
  ]);
  const t = await getTranslations("brand");
  return (
    <>
      <AdminHeader
        section="branding"
        description={t("intro")}
        actions={
          <>
            <ButtonLink href={`/admin/branding/preview?brand=${encodeURIComponent(brand.data.id)}`} variant="secondary" icon="eye">
              {t("previewPage.open")}
            </ButtonLink>
            <ButtonLink href={`/admin/branding/governance?brand=${encodeURIComponent(brand.data.id)}`} variant="secondary" icon="policy">
              {t("governance.open")}
            </ButtonLink>
            <ButtonLink href="/admin/branding/organizations" variant="secondary" icon="building">
              {t("organizations.open")}
            </ButtonLink>
          </>
        }
      />
      <BrandManager
        key={brand.data.id}
        schema={schema.data}
        brands={brands.data}
        brand={brand.data}
        draft={draft.ok ? draft.data : null}
        initialPreview={check.ok ? check.data.preview : null}
        samples={{ ltr: await sample("en"), rtl: await sample("ar") }}
        locales={me.ok ? me.data.tenant.enabledLocales : []}
      />
      {draft.ok && releases.ok ? (
        <ThemeLifecycle
          brandId={brand.data.id}
          draft={draft.data}
          releases={releases.data}
          schedule={schedule.ok ? schedule.data : null}
          approval={approval.ok ? approval.data : null}
          samples={{ ltr: await sample("en"), rtl: await sample("ar") }}
        />
      ) : null}
    </>
  );
}
