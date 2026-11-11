import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { LeavePolicy, LeaveType } from "@/lib/leave";
import { LeaveAdjustments } from "@/features/leave/LeaveAdjustments";
import { LeaveAdmin } from "@/features/leave/LeaveAdmin";

/**
 * Leave types and entitlement policies (spec 022 FR-001), and HR adjustments of a person's balance (section 14). The section appears only when the API lists it; every call
 * is authorized again by the API.
 */
export default async function LeaveAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "leave")) {
    notFound();
  }
  const [types, policies, units] = await Promise.all([
    apiGet<LeaveType[]>("/api/v1/hr/leave/types?all=true"),
    apiGet<LeavePolicy[]>("/api/v1/hr/leave/policies"),
    listUnits(),
  ]);
  if (!types.ok || !policies.ok) {
    notFound();
  }
  const t = await getTranslations("leave.admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <LeaveAdmin initialTypes={types.data} initialPolicies={policies.data} units={units.ok ? units.data : []} />
      <LeaveAdjustments types={types.data} />
    </>
  );
}
