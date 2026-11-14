import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { KioskDevice } from "@/lib/kiosk";
import { KioskAdmin } from "@/features/kiosk/KioskAdmin";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Shared kiosks and badges (spec 030 US4, FR-014, FR-015). The section appears only when the API lists it; every call
 * is authorized again by the API.
 */
export default async function KiosksPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "kiosks")) {
    notFound();
  }
  const [devices, units] = await Promise.all([apiGet<KioskDevice[]>("/api/v1/identity/kiosks"), listUnits()]);
  if (!devices.ok) {
    notFound();
  }
  const t = await getTranslations("kiosk.admin");
  return (
    <>
      <AdminHeader section="kiosks" description={t("intro")} />
      <KioskAdmin initial={devices.data} units={units.ok ? units.data : []} />
    </>
  );
}
