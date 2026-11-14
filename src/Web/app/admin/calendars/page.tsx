import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { Assignment, Calendar } from "@/lib/hr/types";
import { CalendarManager } from "@/features/calendars/CalendarManager";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Working calendars (spec 022 US2): working weeks, public holidays and the units each calendar applies to, with a
 * working-day preview. The section appears only when the API lists it; every call is authorized again by the API.
 */
export default async function CalendarsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "calendars")) {
    notFound();
  }
  const [calendars, assignments, units] = await Promise.all([
    apiGet<Calendar[]>("/api/v1/hr/calendars"),
    apiGet<Assignment[]>("/api/v1/hr/assignments"),
    listUnits(),
  ]);
  if (!calendars.ok || !assignments.ok) {
    notFound();
  }
  const t = await getTranslations("calendars");
  return (
    <>
      <AdminHeader section="calendars" description={t("intro")} />
      <CalendarManager initialCalendars={calendars.data} initialAssignments={assignments.data} units={units.ok ? units.data : []} />
    </>
  );
}
