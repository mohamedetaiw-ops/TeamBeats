import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, learningHref, localText, pathHref, type PathView as Path } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PathView } from "@/features/learning/PathView";

/** A learning path (spec 028 FR-004): its courses in order, with the ones still locked. */
export default async function PathPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const path = await apiGet<Path>(`/api/v1/learning/paths/${id}`);
  if (!path.ok) {
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
          { label: title, href: pathHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={title} eyebrow={t("path.eyebrow")} icon="book" />
        <PathView path={path.data} />
      </main>
    </>
  );
}
