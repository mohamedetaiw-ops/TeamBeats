import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  libraryHref,
  libraryImportHref,
  nameIn,
  type ConnectedSource,
  type FolderView,
  type ImportView,
  type LibraryView,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import {
  ImportList,
  ImportStart,
  ManifestGuide,
} from "@/features/documents/Imports";
import { SharePointImportStart } from "@/features/documents/SharePointImport";

/** Bulk import into a library (spec 011 FR-022, FR-033), for its managers: from a ZIP or from SharePoint, how, and past imports. */
export default async function LibraryImportPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const base = `/api/v1/documents/libraries/${encodeURIComponent(id)}`;
  const [library, folders, imports, sources] = await Promise.all([
    apiGet<LibraryView>(base),
    apiGet<FolderView[]>(`${base}/folders`),
    apiGet<ImportView[]>(`${base}/imports`),
    apiGet<ConnectedSource[]>("/api/v1/documents/connected-sources"),
  ]);
  if (!library.ok || !folders.ok || !imports.ok) {
    notFound();
  }
  const t = await getTranslations("documents.imports");
  const locale = await getLocale();
  const name = nameIn(library.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("documents"), href: "/documents" },
          { label: name, href: libraryHref(id) },
          { label: t("title"), href: libraryImportHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader
          title={t("title")}
          description={t("intro", { library: name })}
          icon="upload"
        />
        <ImportStart libraryId={id} folders={folders.data} />
        {sources.ok && sources.data.some((s) => s.kind === "microsoft365") ? (
          <SharePointImportStart
            libraryId={id}
            folders={folders.data}
            sources={sources.data}
            types={library.data.types}
          />
        ) : null}
        <ImportList imports={imports.data} />
        <ManifestGuide />
      </main>
    </>
  );
}
