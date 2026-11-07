import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { TeamPage } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { MyTeam } from "@/features/people/MyTeam";
import { PeopleNav } from "@/features/people/PeopleNav";

/** My team (spec 010 US3). */
export default async function TeamPageRoute() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const team = await apiGet<TeamPage>("/api/v1/me/team");
  if (!team.ok) {
    notFound();
  }
  const t = await getTranslations("people");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/people" },
          { label: t("team.title"), href: "/people/team" },
        ]}
      />
      <main id="main" className="page people">
        <h1>{t("team.title")}</h1>
        <PeopleNav current="team" />
        <MyTeam initial={team.data} />
      </main>
    </>
  );
}
