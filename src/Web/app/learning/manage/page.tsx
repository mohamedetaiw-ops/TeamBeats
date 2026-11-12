import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { learningHref, manageHref, type CourseSummary, type LmsStatus, type PathSummary } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { LmsPanel } from "@/features/learning/Lms";
import { ManageHome } from "@/features/learning/ManageHome";

/** Building learning (spec 028 US1): courses and paths the viewer manages. The API lists only those in their scope. */
export default async function ManagePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("learning");
  // The LMS panel shows only when the package has the LMS connector and the viewer manages learning for the whole organization.
  const [courses, paths, lms] = await Promise.all([
    apiGet<CourseSummary[]>("/api/v1/learning/manage/courses"),
    apiGet<PathSummary[]>("/api/v1/learning/manage/paths"),
    apiGet<LmsStatus>("/api/v1/learning/manage/lms"),
  ]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("manage.pageTitle"), href: manageHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("manage.pageTitle")} description={t("manage.pageIntro")} icon="edit" />
        {courses.ok ? (
          <>
            <ManageHome courses={courses.data} paths={paths.ok ? paths.data : []} />
            {lms.ok ? <LmsPanel status={lms.data} /> : null}
          </>
        ) : (
          <StateView kind={courses.status === 403 ? "denied" : "error"} title={t("manage.unavailableTitle")} message={t("manage.unavailable")} />
        )}
      </main>
    </>
  );
}
