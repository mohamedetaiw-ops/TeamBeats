import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  libraryHref,
  nameIn,
  type LibrarySummary,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { DocumentsNav } from "@/features/documents/DocumentsNav";

/** The document libraries this person may open (spec 011 US1), at tenant, unit and site scope. */
export default async function LibrariesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const libraries = await apiGet<LibrarySummary[]>(
    "/api/v1/documents/libraries",
  );
  if (!libraries.ok) {
    notFound();
  }
  const t = await getTranslations("documents.libraries");
  const locale = await getLocale();
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[{ label: t("title"), href: "/documents" }]}
      />
      <main id="main" className="page">
        <h1>{t("title")}</h1>
        <p>{t("intro")}</p>
        <DocumentsNav current="libraries" />
        {libraries.data.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : (
          <ul className="documents-cards" data-testid="library-list">
            {libraries.data.map((l) => (
              <li key={l.id} className="documents-card">
                <Link href={libraryHref(l.id)}>
                  <bdi>{nameIn(l.names, locale)}</bdi>
                </Link>
                <p className="documents-meta">{t(`scope.${l.scope}`)}</p>
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
