import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, learningHref, localText, manageHref, pathBuilderHref, type CourseSummary, type PathView } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PathBuilder } from "@/features/learning/PathBuilder";

/** The path builder (spec 028 FR-004): the path's courses in order, prerequisites and audience. */
export default async function PathBuilderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const [path, courses] = await Promise.all([apiGet<PathView>(`/api/v1/learning/paths/${id}`), apiGet<CourseSummary[]>("/api/v1/learning/manage/courses")]);
  if (!path.ok || !path.data.manages) {
    notFound();
  }
  const t = await getTranslations("learning");
  const title = localText(path.data.titles, await getLocale());
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("manage.pageTitle"), href: manageHref },
          { label: title, href: pathBuilderHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={title} eyebrow={t("pathBuilder.eyebrow")} icon="edit" />
        <PathBuilder initial={path.data} courses={courses.ok ? courses.data : []} locales={me.data.tenant.enabledLocales} />
      </main>
    </>
  );
}
