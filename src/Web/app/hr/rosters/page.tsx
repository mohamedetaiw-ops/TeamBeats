import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { weekOf, type RosterWeek, type ShiftPatterns as Patterns } from "@/lib/rosters";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { RosterPlanner } from "@/features/rosters/RosterPlanner";
import { ShiftPatterns } from "@/features/rosters/ShiftPatterns";

/**
 * Shift rosters (spec 022 US10): a manager plans and publishes a week for their direct reports; HR officers covering a
 * team open its week with the manager's ID; HR officers of the whole organization keep the shift patterns here. The
 * API decides who sees and changes what. Weeks start on Monday by default.
 */
export default async function RostersPage({ searchParams }: { searchParams: Promise<{ week?: string; managerId?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const query = await searchParams;
  const week = query.week && /^\d{4}-\d{2}-\d{2}$/.test(query.week) ? query.week : weekOf(new Date().toISOString().slice(0, 10));
  const manager = query.managerId && /^[0-9a-f-]{36}$/i.test(query.managerId) ? `?managerId=${query.managerId}` : "";
  const [patterns, roster] = await Promise.all([apiGet<Patterns>("/api/v1/hr/shift-patterns"), apiGet<RosterWeek>(`/api/v1/hr/rosters/${week}${manager}`)]);
  const t = await getTranslations("rosters");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/hr/rosters" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="calendar" />
        {!patterns.ok ? (
          <p className="notice">{t("notInPackage")}</p>
        ) : (
          <>
            {roster.ok ? <RosterPlanner key={`${week}${manager}`} initial={roster.data} patterns={patterns.data.patterns.filter((p) => p.active)} /> : <p className="notice">{t("notFound")}</p>}
            {patterns.data.canManage ? <ShiftPatterns initial={patterns.data.patterns} /> : null}
          </>
        )}
      </main>
    </>
  );
}
