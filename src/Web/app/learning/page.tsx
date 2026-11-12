import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { catalogueHref, learningHref, manageHref, reportsHref, teamHref, type LearningRights, type MyLearning as Mine, type PathSummary } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { ButtonLink } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { MyLearning } from "@/features/learning/MyLearning";

/** My learning (spec 028 US3): the viewer's courses and paths. The API decides what the viewer was given and may build. */
export default async function LearningPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("learning");
  const [mine, paths, rights] = await Promise.all([
    apiGet<Mine>("/api/v1/learning/me"),
    apiGet<PathSummary[]>("/api/v1/learning/paths"),
    apiGet<LearningRights>("/api/v1/learning/rights"),
  ]);
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("pageTitle"), href: learningHref }]} />
      <main id="main" className="page">
        <PageHeader
          title={t("pageTitle")}
          description={t("pageIntro")}
          icon="book"
          actions={
            <>
              <ButtonLink href={catalogueHref} icon="search">
                {t("catalogue.pageTitle")}
              </ButtonLink>
              {rights.ok && rights.data.manage ? (
                <ButtonLink href={manageHref} icon="edit">
                  {t("manage.pageTitle")}
                </ButtonLink>
              ) : null}
              {rights.ok && rights.data.report ? (
                <ButtonLink href={reportsHref} icon="policy">
                  {t("reports.pageTitle")}
                </ButtonLink>
              ) : null}
              {rights.ok && rights.data.team ? (
                <ButtonLink href={teamHref} icon="people">
                  {t("team.pageTitle")}
                </ButtonLink>
              ) : null}
            </>
          }
        />
        {mine.ok ? (
          <MyLearning mine={mine.data} paths={paths.ok ? paths.data : []} />
        ) : (
          <StateView kind={mine.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
