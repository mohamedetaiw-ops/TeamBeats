import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { TeamAvailability } from "@/features/leave/TeamAvailability";

/** Team availability (spec 022 FR-004): who is away on which day. The API decides who is on the team. */
export default async function TeamLeavePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("leave");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/leave" },
          { label: t("team.title"), href: "/leave/team" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("team.title")} description={t("team.intro")} icon="people" />
        <TeamAvailability />
      </main>
    </>
  );
}
