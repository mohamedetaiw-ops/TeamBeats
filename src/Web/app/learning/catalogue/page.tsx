import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { catalogueHref, learningHref, type CatalogueCourse } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Catalogue } from "@/features/learning/Catalogue";

/** The course catalogue (spec 028 FR-018): published courses that target the viewer, searchable by title. */
export default async function CataloguePage({ searchParams }: { searchParams: Promise<{ q?: string | string[] }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const given = (await searchParams).q;
  const q = ((Array.isArray(given) ? given[0] : given) ?? "").trim().slice(0, 200);
  const t = await getTranslations("learning");
  const courses = await apiGet<CatalogueCourse[]>(`/api/v1/learning/catalogue${q ? `?q=${encodeURIComponent(q)}` : ""}`);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("catalogue.pageTitle"), href: catalogueHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("catalogue.pageTitle")} description={t("catalogue.pageIntro")} icon="book" />
        {courses.ok ? (
          <Catalogue courses={courses.data} q={q} />
        ) : (
          <StateView kind={courses.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
