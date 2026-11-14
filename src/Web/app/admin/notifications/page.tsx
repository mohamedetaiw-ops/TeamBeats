import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { NotificationSettings as Settings } from "@/lib/notifications/types";
import { NotificationSettings } from "@/features/notifications/NotificationSettings";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Tenant notification settings (spec 015 US4). The section appears only when the API lists it, and the settings API
 * authorizes every call again; the page decides nothing about access.
 */
export default async function NotificationSettingsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "notifications")) {
    notFound();
  }
  const settings = await apiGet<Settings>("/api/v1/notifications/settings");
  if (!settings.ok) {
    notFound();
  }
  const t = await getTranslations("notifications.settings");
  return (
    <>
      <AdminHeader section="notifications" description={t("intro")} />
      <NotificationSettings initial={settings.data} />
    </>
  );
}
