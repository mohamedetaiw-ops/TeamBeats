import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AdminAppointment, MembershipPolicy, TenantSettings } from "@/lib/admin/types";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { TenantSettingsAdmin } from "@/features/admin/TenantSettingsAdmin";
import { MembershipPoliciesAdmin } from "@/features/admin/MembershipPoliciesAdmin";

/**
 * Workspace settings (spec 001 US2, US3). The section appears only when the API lists it; the administrators list is
 * shown only when the API returns it to this caller, and every change is authorized again by the API.
 */
export default async function TenantSettingsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "tenant_settings")) {
    notFound();
  }
  const [settings, admins, policies] = await Promise.all([
    apiGet<TenantSettings>("/api/v1/tenant/settings"),
    apiGet<AdminAppointment[]>("/api/v1/tenant/admins"),
    apiGet<MembershipPolicy[]>("/api/v1/tenant/membership-policies"),
  ]);
  if (!settings.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <AdminHeader section="tenant_settings" description={t("tenant.description")} />
      <div className="admin-screen">
        <TenantSettingsAdmin initial={settings.data} initialAdmins={admins.ok ? admins.data : null} />
        {policies.ok ? <MembershipPoliciesAdmin initial={policies.data} /> : null}
      </div>
    </>
  );
}
