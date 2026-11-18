import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { reportHref, type ReportView } from "@/lib/analytics/reports";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ReportDetail } from "@/features/analytics/Reports";
import "@/features/analytics/analytics.css";

/** One report (spec 021 T016, US7), for its recipient only; anyone else gets 404 from the API. */
export default async function AnalyticsReportPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<ReportView>(`/api/v1/analytics/reports/${encodeURIComponent(id)}`);
  if (!view.ok && view.status === 404) notFound();
  const t = await getTranslations("analytics.reports");
  const title = view.ok ? view.data.report.name : t("title");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("workspace"), href: "/analytics" },
          { label: t("title"), href: "/analytics/reports" },
          { label: title, href: reportHref(id) },
        ]}
      />
      <main id="main" className="page analytics-report-page">
        <PageHeader title={title} description={t("detailIntro")} icon="files" />
        {view.ok ? <ReportDetail view={view.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
      </main>
    </>
  );
}
