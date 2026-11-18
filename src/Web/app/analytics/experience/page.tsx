import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ExperienceAnalyticsView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ExperienceDashboard } from "@/features/analytics/ExperienceAnalytics";
import "@/features/analytics/analytics.css";

/** Experience dashboards (spec 021 T015, US9) for the last 30 days. The API needs analytics.view for the whole tenant. */
export default async function ExperienceAnalyticsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<ExperienceAnalyticsView>("/api/v1/analytics/experience");
  const t = await getTranslations("analytics.experience");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/experience" }]} />
      <main id="main" className="page analytics-experience">
        <PageHeader title={t("title")} description={t("intro")} icon="trend-up" />
        {view.ok ? <ExperienceDashboard view={view.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
      </main>
    </>
  );
}
