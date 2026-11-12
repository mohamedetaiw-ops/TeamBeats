import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { surveysHref, type SurveyList, type SurveySettingsView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { SurveyDirectory } from "@/features/engagement/Surveys";

/** Surveys (spec 023 US3): the ones to answer, the ones the viewer manages and the builder. The API decides all of it. */
export default async function SurveysPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("engagement");
  const [list, settings] = await Promise.all([apiGet<SurveyList>("/api/v1/engagement/surveys"), apiGet<SurveySettingsView>("/api/v1/engagement/settings/surveys")]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: t("surveys.pageTitle"), href: surveysHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("surveys.pageTitle")} description={t("surveys.pageIntro")} icon="tasks" />
        {list.ok ? (
          <SurveyDirectory list={list.data} settings={settings.ok ? settings.data : null} />
        ) : (
          <StateView kind={list.status === 403 ? "unsupported" : "error"} title={t("surveys.unavailableTitle")} message={t("surveys.unavailable")} />
        )}
      </main>
    </>
  );
}
