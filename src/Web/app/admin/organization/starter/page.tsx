import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import type { OrgUnitView, UserSummary } from "@/lib/admin/types";
import { listBrands } from "@/lib/brand/client";
import { getCatalogue } from "@/lib/experience/client";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { PageHeader } from "@/components/ui/PageHeader";
import { StarterWizard } from "@/features/admin/StarterWizard";

/**
 * Starter wizard for a new organization (spec 033 R2 T020, FR-027). The page shows for administrators who may edit
 * brands; the API decides, for the organization chosen, whether they may run it.
 */
export default async function StarterPage({ searchParams }: { searchParams: Promise<{ unit?: string }> }) {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "organization")) {
    notFound();
  }
  const [units, users, brands, catalogue, me] = await Promise.all([
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
    apiGet<UserSummary[]>("/api/v1/admin/users"),
    listBrands(),
    getCatalogue(),
    apiGet<Me>("/api/v1/me"),
  ]);
  if (!units.ok || !brands.ok || !catalogue.ok) {
    notFound();
  }
  const { unit } = await searchParams;
  const t = await getTranslations("admin");
  const templates = catalogue.data.templates
    .filter((tp) => tp.state === "released")
    .map((tp) => ({ id: tp.id, name: tp.name, description: tp.description }));
  return (
    <>
      <Breadcrumb
        label={t("org.starter.breadcrumb")}
        crumbs={[
          { label: t("nav.organization"), href: "/admin/organization" },
          { label: t("org.starter.title"), href: "/admin/organization/starter" },
        ]}
      />
      <PageHeader title={t("org.starter.title")} description={t("org.starter.description")} icon="spark" />
      <StarterWizard
        units={units.data}
        users={users.ok ? users.data : []}
        brands={brands.data.map((b) => ({ id: b.id, name: b.name }))}
        templates={templates}
        locales={me.ok ? me.data.tenant.enabledLocales : ["en"]}
        initialUnit={units.data.some((u) => u.id === unit) ? unit! : ""}
      />
    </>
  );
}
