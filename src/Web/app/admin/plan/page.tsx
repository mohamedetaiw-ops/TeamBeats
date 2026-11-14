import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { Entitlements } from "@/lib/admin/types";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { PlanAdmin } from "@/features/admin/PlanAdmin";

/** Plan (spec 005 US3): the subscribed package and its capabilities. The section appears only when the API lists it. */
export default async function PlanPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "plan")) {
    notFound();
  }
  const plan = await apiGet<Entitlements>("/api/v1/tenant/entitlements");
  if (!plan.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <AdminHeader section="plan" description={t("plan.description")} />
      <PlanAdmin plan={plan.data} />
    </>
  );
}
