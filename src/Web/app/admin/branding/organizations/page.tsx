import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getTokenSchema, listBrands } from "@/lib/brand/client";
import { listUnits } from "@/lib/experience/client";
import { unitName } from "@/lib/experience/builder-types";
import type { BrandAssignment, ThemeLock } from "@/lib/brand/types";
import { OrganizationThemes } from "@/features/brand/OrganizationThemes";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { ButtonLink } from "@/components/ui/Button";
import "@/features/brand/brand.css";

/**
 * Organization themes (spec 033 FR-002, FR-019): the brand, own token values and locks of the tenant and each active
 * organization. Each part shows what the API answers for this person; the page decides nothing about access.
 */
export default async function OrganizationThemesPage({ searchParams }: { searchParams: Promise<{ scope?: string }> }) {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "branding")) {
    notFound();
  }
  const [schema, brands, units] = await Promise.all([getTokenSchema(), listBrands(), listUnits()]);
  if (!schema.ok) {
    notFound();
  }
  const t = await getTranslations("brand.organizations");
  const locale = await getLocale();
  const scopes = [
    { value: "tenant", label: t("tenant") },
    ...(units.ok ? units.data.filter((u) => u.status === "active").map((u) => ({ value: u.id, label: unitName(u, locale) })) : []),
  ];
  const { scope: requested } = await searchParams;
  const scope = scopes.find((s) => s.value === requested)?.value ?? "tenant";
  const path = encodeURIComponent(scope);
  const [assignment, locks] = await Promise.all([
    apiGet<BrandAssignment>(`/api/v1/experience/brand-assignments/${path}`),
    apiGet<ThemeLock[]>(`/api/v1/experience/locks/${path}`),
  ]);
  return (
    <>
      <AdminHeader
        section="branding"
        title={t("title")}
        description={t("intro")}
        actions={
          <ButtonLink href="/admin/branding" variant="secondary" icon="chevron-back">
            {t("back")}
          </ButtonLink>
        }
      />
      <OrganizationThemes
        scopes={scopes}
        brands={brands.ok ? brands.data.map((b) => ({ id: b.id, name: b.name })) : []}
        tokens={schema.data.tokens}
        shellSettings={schema.data.shellSettings}
        initialScope={scope}
        initialAssignment={assignment.ok ? assignment.data : null}
        initialLocks={locks.ok ? locks.data : null}
      />
    </>
  );
}
