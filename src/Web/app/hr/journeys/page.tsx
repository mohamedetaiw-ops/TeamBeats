import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { JourneySummary } from "@/lib/journeys";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { MyJourneys } from "@/features/journeys/MyJourneys";

/** My journeys (spec 022 US4, US5): the person's own onboarding, offboarding, transfer and promotion journeys. */
export default async function MyJourneysPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const mine = await apiGet<JourneySummary[]>("/api/v1/me/journeys");
  const t = await getTranslations("journeys.page");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/hr/journeys" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="tasks" />
        {mine.ok ? <MyJourneys journeys={mine.data} /> : <p className="notice">{t("notInPackage")}</p>}
      </main>
    </>
  );
}
