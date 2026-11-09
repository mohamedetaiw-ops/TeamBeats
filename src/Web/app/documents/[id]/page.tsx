import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  documentHref,
  libraryHref,
  nameIn,
  type DocumentView,
  type LibraryView,
  type VersionView,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { DocumentsNav } from "@/features/documents/DocumentsNav";
import { DocumentPanel } from "@/features/documents/DocumentPanel";

/**
 * A document (spec 011 US1 to US3): its metadata, preview, versions and the actions the API allows this person. Readers
 * who may not see it, and other tenants, get not found.
 */
export default async function DocumentPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const doc = await apiGet<DocumentView>(
    `/api/v1/documents/${encodeURIComponent(id)}`,
  );
  if (!doc.ok) {
    notFound();
  }
  const [library, versions] = await Promise.all([
    apiGet<LibraryView>(
      `/api/v1/documents/libraries/${encodeURIComponent(doc.data.libraryId)}`,
    ),
    apiGet<VersionView[]>(
      `/api/v1/documents/${encodeURIComponent(id)}/versions`,
    ),
  ]);
  const t = await getTranslations("documents.libraries");
  const locale = await getLocale();
  const libraryName = library.ok ? nameIn(library.data.names, locale) : "";
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/documents" },
          ...(library.ok
            ? [{ label: libraryName, href: libraryHref(doc.data.libraryId) }]
            : []),
          { label: doc.data.name, href: documentHref(id) },
        ]}
      />
      <main id="main" className="page">
        <DocumentsNav current="libraries" />
        <DocumentPanel
          initial={doc.data}
          versions={versions.ok ? versions.data : []}
          types={library.ok ? library.data.types : []}
          me={me.data.id}
        />
      </main>
    </>
  );
}
