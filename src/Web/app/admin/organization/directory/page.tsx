import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { DirectoryMappingState, OrgUnitView } from "@/lib/admin/types";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { PageHeader } from "@/components/ui/PageHeader";
import { DirectoryMappingAdmin } from "@/features/admin/DirectoryMappingAdmin";

/**
 * Organization from the directory (spec 002 US5). Anyone the API lets read the hierarchy sees the mapping and its runs;
 * the controls are offered when the organization section covers the whole workspace, and the API checks each change.
 */
export default async function DirectoryMappingPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  const section = nav.ok ? nav.data.find((s) => s.key === "organization") : undefined;
  if (!section) {
    notFound();
  }
  const [state, units] = await Promise.all([apiGet<DirectoryMappingState>("/api/v1/org/directory-mapping"), apiGet<OrgUnitView[]>("/api/v1/org/units")]);
  if (!state.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <Breadcrumb
        label={t("org.directory.breadcrumb")}
        crumbs={[
          { label: t("nav.organization"), href: "/admin/organization" },
          { label: t("org.directory.title"), href: "/admin/organization/directory" },
        ]}
      />
      <PageHeader title={t("org.directory.title")} description={t("org.directory.description")} icon="people" />
      <DirectoryMappingAdmin initial={state.data} units={units.ok ? units.data : []} wholeTenant={section.wholeTenant} />
    </>
  );
}
