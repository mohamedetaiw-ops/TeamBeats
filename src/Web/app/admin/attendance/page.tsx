import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { AttendanceSettings, WorkRule } from "@/lib/attendance";
import type { LeaveType } from "@/lib/leave";
import { AttendanceAdmin } from "@/features/attendance/AttendanceAdmin";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Attendance settings, working hours rules and the payroll export (spec 022 FR-010, FR-012). The section appears only
 * when the API lists it; when the plan lacks attendance the API refuses and the page says so. Every call is authorized again by the API.
 */
export default async function AttendanceAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "attendance")) {
    notFound();
  }
  const [settings, rules, units, leaveTypes] = await Promise.all([
    apiGet<AttendanceSettings>("/api/v1/hr/attendance-settings"),
    apiGet<WorkRule[]>("/api/v1/hr/work-rules"),
    listUnits(),
    apiGet<LeaveType[]>("/api/v1/hr/leave/types?all=true"),
  ]);
  const t = await getTranslations("attendance.admin");
  return (
    <>
      <AdminHeader section="attendance" description={t("intro")} />
      {settings.ok && rules.ok ? (
        <AttendanceAdmin settings={settings.data} rules={rules.data} units={units.ok ? units.data : []} leaveTypes={leaveTypes.ok ? leaveTypes.data : []} />
      ) : (
        <p className="notice">{t("notInPackage")}</p>
      )}
    </>
  );
}
