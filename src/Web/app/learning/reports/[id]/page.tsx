import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { learningHref, localText, reportHref, reportsHref, type CourseReport } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CourseReportView } from "@/features/learning/Reports";

/** One course's report (spec 028 US5): counts per unit in the viewer's scope, people and the evidence export. */
export default async function CourseReportPage({ params }: { params: Promise<{ id: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { id } = await params;
  const t = await getTranslations("learning");
  const locale = await getLocale();
  const report = await apiGet<CourseReport>(`/api/v1/learning/reports/courses/${encodeURIComponent(id)}`);
  if (!report.ok && report.status === 404) {
    notFound();
  }
  const title = report.ok ? localText(report.data.titles, locale) : t("reports.pageTitle");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("reports.pageTitle"), href: reportsHref },
          { label: title, href: reportHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={title} description={t("reports.courseIntro")} icon="policy" />
        {report.ok ? (
          <CourseReportView report={report.data} />
        ) : (
          <StateView kind={report.status === 403 ? "denied" : "error"} title={t("reports.unavailableTitle")} message={t("reports.unavailable")} />
        )}
      </main>
    </>
  );
}
