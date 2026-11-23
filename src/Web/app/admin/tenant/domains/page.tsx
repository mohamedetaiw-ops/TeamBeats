import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { PageHeader } from "@/components/ui/PageHeader";
import { DomainsAdmin, type CustomDomain } from "@/features/admin/DomainsAdmin";

/**
 * Custom domains (spec 033 R2 T021, FR-010) under the workspace settings. Anyone the API lets read the settings sees the
 * list; the API checks every change and says so when it is not allowed.
 */
export default async function DomainsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "tenant_settings")) {
    notFound();
  }
  const [domains, me] = await Promise.all([apiGet<CustomDomain[]>("/api/v1/tenant/domains"), apiGet<Me>("/api/v1/me")]);
  if (!domains.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <Breadcrumb
        label={t("tenant.domains.breadcrumb")}
        crumbs={[
          { label: t("nav.tenant_settings"), href: "/admin/tenant" },
          { label: t("tenant.domains.title"), href: "/admin/tenant/domains" },
        ]}
      />
      <PageHeader title={t("tenant.domains.title")} description={t("tenant.domains.pageDescription")} icon="globe" />
      <DomainsAdmin initial={domains.data} locales={me.ok ? me.data.tenant.enabledLocales : ["en"]} />
    </>
  );
}
