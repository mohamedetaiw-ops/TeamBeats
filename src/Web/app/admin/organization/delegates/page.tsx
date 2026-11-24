import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { OrgUnitView, UserSummary } from "@/lib/admin/types";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { PageHeader } from "@/components/ui/PageHeader";
import { DelegationAdmin, type Delegation } from "@/features/admin/DelegationAdmin";

/**
 * Delegates per organization (spec 033 R2 T019, FR-020). The page shows when the API lists the delegations, which it does
 * for an administrator who may assign roles somewhere and whose package delegates administration.
 */
export default async function DelegatesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "organization")) {
    notFound();
  }
  const [delegations, units, users] = await Promise.all([
    apiGet<Delegation[]>("/api/v1/experience/delegations"),
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
    apiGet<UserSummary[]>("/api/v1/admin/users"),
  ]);
  if (!delegations.ok || !units.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <Breadcrumb
        label={t("org.delegates.breadcrumb")}
        crumbs={[
          { label: t("nav.organization"), href: "/admin/organization" },
          { label: t("org.delegates.title"), href: "/admin/organization/delegates" },
        ]}
      />
      <PageHeader title={t("org.delegates.title")} description={t("org.delegates.description")} icon="people" />
      <DelegationAdmin initial={delegations.data} units={units.data} users={users.ok ? users.data : []} />
    </>
  );
}
