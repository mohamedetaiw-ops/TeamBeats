import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  libraryHref,
  nameIn,
  type FolderView,
  type LibraryView,
  type SavedView,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { DocumentsNav } from "@/features/documents/DocumentsNav";
import { LibraryBrowser } from "@/features/documents/LibraryBrowser";

/** One library (spec 011 US1): folders, filters on the type's fields, sorting, saved views and upload. */
export default async function LibraryPage({
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
  const [library, folders, views] = await Promise.all([
    apiGet<LibraryView>(base),
    apiGet<FolderView[]>(`${base}/folders`),
    apiGet<SavedView[]>(`${base}/views`),
  ]);
  if (!library.ok || !folders.ok || !views.ok) {
    notFound();
  }
  const t = await getTranslations("documents.libraries");
  const locale = await getLocale();
  const name = nameIn(library.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/documents" },
          { label: name, href: libraryHref(id) },
        ]}
      />
      <main id="main" className="page">
        <h1>
          <bdi>{name}</bdi>
        </h1>
        <DocumentsNav current="libraries" />
        <LibraryBrowser
          library={library.data}
          folders={folders.data}
          views={views.data}
        />
      </main>
    </>
  );
}
