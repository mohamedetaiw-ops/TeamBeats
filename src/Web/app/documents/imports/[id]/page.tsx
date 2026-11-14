import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  importApi,
  importHref,
  importRowStates,
  libraryHref,
  libraryImportHref,
  nameIn,
  type ImportRowPage,
  type ImportRowState,
  type ImportView,
  type LibraryView,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { ImportReport } from "@/features/documents/Imports";

/** The report of one bulk import (spec 011 FR-022): progress, totals per result and each row's result. */
export default async function ImportReportPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ status?: string }> }) {
  const { id } = await params;
  const { status: wanted } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const status = importRowStates.includes(wanted as ImportRowState) ? (wanted as ImportRowState) : null;
  const [view, page] = await Promise.all([
    apiGet<ImportView>(importApi(id)),
    apiGet<ImportRowPage>(`${importApi(id)}/rows?limit=500${status ? `&status=${status}` : ""}`),
  ]);
  if (!view.ok || !page.ok) {
    notFound();
  }
  const library = await apiGet<LibraryView>(`/api/v1/documents/libraries/${encodeURIComponent(view.data.libraryId)}`);
  const t = await getTranslations("documents.imports");
  const locale = await getLocale();
  const libraryName = library.ok ? nameIn(library.data.names, locale) : t("library");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("documents"), href: "/documents" },
          { label: libraryName, href: libraryHref(view.data.libraryId) },
          { label: t("title"), href: libraryImportHref(view.data.libraryId) },
          { label: view.data.fileName, href: importHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("reportTitle", { file: view.data.fileName })} description={t(view.data.source === "sharepoint" ? "reportIntroSharePoint" : "reportIntro")} icon="upload" />
        <ImportReport view={view.data} page={page.data} status={status} />
      </main>
    </>
  );
}
