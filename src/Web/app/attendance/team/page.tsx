import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { TeamAttendance } from "@/features/attendance/TeamAttendance";

/** My team's attendance (spec 022 US3 scenario 5). The API decides who is on the team. */
export default async function TeamAttendancePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("attendance");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/attendance" },
          { label: t("team.title"), href: "/attendance/team" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("team.title")} description={t("team.intro")} icon="people" />
        <TeamAttendance />
      </main>
    </>
  );
}
