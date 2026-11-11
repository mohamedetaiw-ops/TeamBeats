import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AdminAppointment, TenantSettings } from "@/lib/admin/types";
import { PageHeader } from "@/components/ui/PageHeader";
import { TenantSettingsAdmin } from "@/features/admin/TenantSettingsAdmin";

/**
 * Workspace settings (spec 001 US2, US3). The section appears only when the API lists it; the administrators list is
 * shown only when the API returns it to this caller, and every change is authorized again by the API.
 */
export default async function TenantSettingsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "tenant_settings")) {
    notFound();
  }
  const [settings, admins] = await Promise.all([apiGet<TenantSettings>("/api/v1/tenant/settings"), apiGet<AdminAppointment[]>("/api/v1/tenant/admins")]);
  if (!settings.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <PageHeader title={t("nav.tenant_settings")} description={t("tenant.description")} icon="settings" />
      <TenantSettingsAdmin initial={settings.data} initialAdmins={admins.ok ? admins.data : null} />
    </>
  );
}
