import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { builderHref, isGuid, learningHref, localText, manageHref, type CourseDetail } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { CourseBuilder } from "@/features/learning/CourseBuilder";

/** The course builder (spec 028 US1): lessons, quizzes, audience, publish and retire, and the course's learners. */
export default async function CourseBuilderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const course = await apiGet<CourseDetail>(`/api/v1/learning/manage/courses/${id}`);
  if (!course.ok) {
    notFound();
  }
  const t = await getTranslations("learning");
  const title = localText(course.data.draft.titles, await getLocale());
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("manage.pageTitle"), href: manageHref },
          { label: title, href: builderHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={title} eyebrow={t("builder.eyebrow")} icon="edit" />
        <CourseBuilder initial={course.data} locales={me.data.tenant.enabledLocales} />
      </main>
    </>
  );
}
