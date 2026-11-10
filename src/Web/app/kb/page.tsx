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
import { PageHeader } from "@/components/ui/PageHeader";
import { TileCard } from "@/components/ui/TileCard";
import { Badge } from "@/components/ui/Badge";

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
        <PageHeader
          title={t("title")}
          description={t("intro")}
          icon="book"
          nav={<DocumentsNav current="kb" />}
          actions={
            <Link className="button" href="/kb/new">
              {t("new")}
            </Link>
          }
        />
        {articles.data.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : (
          <ul className="ui-tiles" data-testid="article-list">
            {articles.data.map((a) => (
              <li key={a.id}>
                <TileCard
                  title={nameIn(a.titles, locale)}
                  href={articleHref(a.id)}
                  icon="book"
                  meta={
                    <time dateTime={a.publishedAt}>
                      {t("updated", { date: format.dateTime(new Date(a.publishedAt), { dateStyle: "medium" }) })}
                    </time>
                  }
                  footer={a.template ? <Badge tone="accent">{t(`template.${a.template}`)}</Badge> : null}
                />
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
