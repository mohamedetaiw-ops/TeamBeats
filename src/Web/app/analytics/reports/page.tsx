import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ReportList } from "@/lib/analytics/reports";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ReportsList } from "@/features/analytics/Reports";
import "@/features/analytics/analytics.css";

/** Scheduled reports and exports (spec 021 T016, US7): the person's own reports and schedules. The API answers 403 without analytics.view. */
export default async function AnalyticsReportsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const list = await apiGet<ReportList>("/api/v1/analytics/reports");
  const t = await getTranslations("analytics.reports");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/reports" }]} />
      <main id="main" className="page analytics-reports-page">
        <PageHeader title={t("title")} description={t("intro")} icon="files" />
        {list.ok ? (
          <ReportsList list={list.data} />
        ) : (
          <StateView kind={list.status === 403 ? "denied" : "error"} title={t(list.status === 403 ? "denied" : "error")} message={t(list.status === 403 ? "deniedHint" : "errorHint")} />
        )}
      </main>
    </>
  );
}
