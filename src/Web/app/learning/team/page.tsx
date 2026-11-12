import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { learningHref, teamHref, type CatalogueCourse, type TeamMember } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Team } from "@/features/learning/Team";

/** My team's learning (spec 028 US5 scenario 3): direct reports only, as the API decides. */
export default async function TeamPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("learning");
  const [team, courses] = await Promise.all([apiGet<TeamMember[]>("/api/v1/learning/team"), apiGet<CatalogueCourse[]>("/api/v1/learning/catalogue")]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("team.pageTitle"), href: teamHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("team.pageTitle")} description={t("team.pageIntro")} icon="people" />
        {team.ok ? (
          <Team initial={team.data} courses={courses.ok ? courses.data : []} />
        ) : (
          <StateView kind={team.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
