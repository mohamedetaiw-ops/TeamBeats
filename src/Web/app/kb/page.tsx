import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  articleHref,
  nameIn,
  type ArticleSummary,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { DocumentsNav } from "@/features/documents/DocumentsNav";

/** The knowledge base (spec 011 US4): published articles this person may read, the most recent first. */
export default async function KnowledgeBasePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const articles = await apiGet<ArticleSummary[]>("/api/v1/kb/articles");
  if (!articles.ok) {
    notFound();
  }
  const t = await getTranslations("documents.kb");
  const locale = await getLocale();
  const format = await getFormatter();
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/kb" }]} />
      <main id="main" className="page">
        <h1>{t("title")}</h1>
        <p>{t("intro")}</p>
        <DocumentsNav current="kb" />
        <p>
          <Link className="button" href="/kb/new">
            {t("new")}
          </Link>
        </p>
        {articles.data.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : (
          <ul className="documents-cards" data-testid="article-list">
            {articles.data.map((a) => (
              <li key={a.id} className="documents-card">
                <Link href={articleHref(a.id)}>
                  <bdi>{nameIn(a.titles, locale)}</bdi>
                </Link>
                <p className="documents-meta">
                  {a.template ? (
                    <span className="documents-badge">
                      {t(`template.${a.template}`)}
                    </span>
                  ) : null}
                  <time dateTime={a.publishedAt}>
                    {format.dateTime(new Date(a.publishedAt), {
                      dateStyle: "medium",
                    })}
                  </time>
                </p>
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
