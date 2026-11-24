import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { OrgUnitView, UnitType } from "@/lib/admin/types";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { ButtonLink } from "@/components/ui/Button";
import { OrganizationAdmin } from "@/features/admin/OrganizationAdmin";

/**
 * Organization (spec 002 US1 to US4): the unit tree the caller may see, with units, memberships and unit types. The
 * section appears only when the API lists it; the tree holds only the units the API returns, and every change is
 * authorized again by the API for the unit it touches.
 */
export default async function OrganizationPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "organization")) {
    notFound();
  }
  const [units, types, delegations, brands] = await Promise.all([
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
    apiGet<UnitType[]>("/api/v1/org/unit-types"),
    apiGet<unknown[]>("/api/v1/experience/delegations"),
    apiGet<unknown[]>("/api/v1/experience/brands"),
  ]);
  if (!units.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <AdminHeader
        section="organization"
        description={t("org.description")}
        actions={
          <>
            {brands.ok ? (
              <ButtonLink href="/admin/organization/starter" variant="secondary" icon="spark">
                {t("org.starter.open")}
              </ButtonLink>
            ) : null}
            {delegations.ok ? (
              <ButtonLink href="/admin/organization/delegates" variant="secondary" icon="people">
                {t("org.delegates.open")}
              </ButtonLink>
            ) : null}
            <ButtonLink href="/admin/organization/directory" variant="secondary" icon="link">
              {t("org.directory.open")}
            </ButtonLink>
          </>
        }
      />
      <OrganizationAdmin initialUnits={units.data} types={types.ok ? types.data : []} />
    </>
  );
}
