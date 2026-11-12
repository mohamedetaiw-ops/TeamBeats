import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { courseHref, courseLocale, isGuid, learningApi, learningHref, localText, type LearnerCourse } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { Player } from "@/features/learning/Player";

/** A course (spec 028 US3): enrol, take its lessons and quizzes, and resume where you left off. Opened from a path, it starts through the path. */
export default async function CoursePage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ path?: string }> }) {
  const { id } = await params;
  const { path } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const course = await apiGet<LearnerCourse>(`/api/v1/learning/courses/${id}`);
  if (!course.ok) {
    notFound();
  }
  const t = await getTranslations("learning");
  const locale = await getLocale();
  const title = localText(course.data.titles, courseLocale(course.data.locales, locale));
  const startPath = path && isGuid(path) ? `${learningApi}/paths/${path}/courses/${id}:start` : undefined;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: title, href: courseHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={title} eyebrow={course.data.category ?? undefined} icon="book" />
        <Player initial={course.data} startPath={startPath} />
      </main>
    </>
  );
}
