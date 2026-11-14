import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { SavedSearchList } from "@/features/search/SavedSearchList";
import "@/features/search/search.css";

/** The person's saved searches and their daily alerts (spec 012 US7, FR-019). */
export default async function SavedSearchesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("search");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/search" },
          { label: t("saved.title"), href: "/search/saved" },
        ]}
      />
      <main id="main" className="page search-page">
        <PageHeader title={t("saved.title")} description={t("saved.description")} icon="search" />
        <SavedSearchList />
      </main>
    </>
  );
}
