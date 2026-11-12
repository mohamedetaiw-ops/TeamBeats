import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, surveyHref, surveysHref, type SurveyList, type SurveyView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { SurveyDetail } from "@/features/engagement/Surveys";

/** One survey (spec 023 US3): build, answer or read its results, as the API allows. */
export default async function SurveyPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const [survey, list] = await Promise.all([apiGet<SurveyView>(`/api/v1/engagement/surveys/${id}`), apiGet<SurveyList>("/api/v1/engagement/surveys")]);
  if (!survey.ok) {
    notFound();
  }
  const t = await getTranslations("engagement");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: t("surveys.pageTitle"), href: surveysHref },
          { label: survey.data.title, href: surveyHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={survey.data.title} icon="tasks" />
        <SurveyDetail initial={survey.data} defaultThreshold={list.ok ? list.data.defaultThreshold : 5} />
      </main>
    </>
  );
}
