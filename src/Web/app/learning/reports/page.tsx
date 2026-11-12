import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { learningHref, reportsHref, type ReportCourse } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ReportCourses } from "@/features/learning/Reports";

/** Learning reports (spec 028 US5): the courses a compliance officer can report on. The API refuses anyone without a report scope. */
export default async function ReportsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("learning");
  const courses = await apiGet<ReportCourse[]>("/api/v1/learning/reports/courses");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("reports.pageTitle"), href: reportsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("reports.pageTitle")} description={t("reports.pageIntro")} icon="policy" />
        {courses.ok ? (
          <ReportCourses courses={courses.data} />
        ) : (
          <StateView kind={courses.status === 403 ? "denied" : "error"} title={t("reports.unavailableTitle")} message={t("reports.unavailable")} />
        )}
      </main>
    </>
  );
}
