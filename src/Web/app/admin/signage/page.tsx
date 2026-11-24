import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { SignageScreen } from "@/lib/notifications/types";
import { SignageAdmin } from "@/features/notifications/SignageAdmin";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Signage screens of a location (spec 015 US7). The section appears only when the API lists it; every call is
 * authorized again by the API, which also decides which screens the person sees.
 */
export default async function SignagePage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "signage")) {
    notFound();
  }
  const [screens, units] = await Promise.all([apiGet<SignageScreen[]>("/api/v1/notifications/signage-screens"), listUnits()]);
  if (!screens.ok) {
    notFound();
  }
  const t = await getTranslations("notifications.signage");
  return (
    <>
      <AdminHeader section="signage" description={t("intro")} />
      <SignageAdmin initial={screens.data} units={units.ok ? units.data : []} />
    </>
  );
}
