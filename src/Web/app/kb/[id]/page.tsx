import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  articleHref,
  documentHref,
  nameIn,
  type ArticleView,
  type FeedbackReport,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { DocumentsNav } from "@/features/documents/DocumentsNav";
import { ArticleFeedback } from "@/features/documents/ArticleFeedback";

/**
 * A knowledge article (spec 011 US4): its sections in the reader's language with anchors that citation links point at,
 * related articles and documents the reader can open, the helpfulness question, and for the owner and knowledge
 * editors the score and comments. Articles the reader may not read are not found.
 */
export default async function ArticlePage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const article = await apiGet<ArticleView>(
    `/api/v1/kb/articles/${encodeURIComponent(id)}`,
  );
  if (!article.ok) {
    notFound();
  }
  const a = article.data;
  const report = a.canManage
    ? await apiGet<FeedbackReport>(
        `/api/v1/kb/articles/${encodeURIComponent(id)}/feedback`,
      )
    : null;
  const t = await getTranslations("documents.kb");
  const locale = await getLocale();
  const format = await getFormatter();
  const title = a.titles[a.locale] ?? nameIn(a.titles, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/kb" },
          { label: title, href: articleHref(a.id) },
        ]}
      />
      <main id="main" className="page">
        <DocumentsNav current="kb" />
        <article lang={a.locale} className="documents-article">
          <h1>
            <bdi>{title}</bdi>
          </h1>
          <p className="documents-meta">
            {a.template ? (
              <span className="documents-badge">
                {t(`template.${a.template}`)}
              </span>
            ) : null}
            {t("updated", {
              date: format.dateTime(new Date(a.publishedAt), {
                dateStyle: "long",
              }),
              version: a.version,
            })}
          </p>
          {a.canManage ? (
            <p>
              <Link href={`/content/items/${encodeURIComponent(a.id)}`}>
                {t("edit")}
              </Link>
            </p>
          ) : null}
          {a.sections.length > 1 ? (
            <nav aria-label={t("contents")} className="documents-toc">
              <ol>
                {a.sections.map((s) => (
                  <li key={s.anchor}>
                    <a href={`#${s.anchor}`}>
                      <bdi>{s.heading}</bdi>
                    </a>
                  </li>
                ))}
              </ol>
            </nav>
          ) : null}
          {a.sections.map((s) => {
            const Tag = `h${Math.min(Math.max(s.level, 2), 4)}` as
              | "h2"
              | "h3"
              | "h4";
            return (
              <section
                key={s.anchor}
                aria-labelledby={s.anchor}
                className="documents-section"
              >
                <Tag id={s.anchor}>
                  <bdi>{s.heading}</bdi>
                </Tag>
                {s.text
                  .split("\n")
                  .filter((line) => line.trim())
                  .map((line, i) => (
                    <p key={i}>
                      <bdi>{line}</bdi>
                    </p>
                  ))}
              </section>
            );
          })}
        </article>

        {a.relatedArticles.length > 0 || a.relatedDocuments.length > 0 ? (
          <section className="documents-card" aria-label={t("related")}>
            <h2>{t("related")}</h2>
            <ul>
              {a.relatedArticles.map((r) => (
                <li key={r.id}>
                  <Link href={articleHref(r.id)}>
                    <bdi>{nameIn(r.titles, locale)}</bdi>
                  </Link>
                </li>
              ))}
              {a.relatedDocuments.map((d) => (
                <li key={d.id}>
                  <Link href={documentHref(d.id)}>
                    <bdi>{d.number ? `${d.number} ${d.name}` : d.name}</bdi>
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        <ArticleFeedback articleId={a.id} initial={a.myFeedback} />

        {a.score ? (
          <section
            className="documents-card"
            aria-label={t("score")}
            data-testid="article-score"
          >
            <h2>{t("score")}</h2>
            <p>
              {a.score.percent === null
                ? t("noScore")
                : t("scoreValue", {
                    percent: a.score.percent,
                    helpful: a.score.helpful,
                    total: a.score.helpful + a.score.notHelpful,
                  })}
            </p>
            {report?.ok && report.data.comments.length > 0 ? (
              <ul className="documents-comments">
                {report.data.comments.map((c, i) => (
                  <li key={i}>
                    <span className="documents-badge">
                      {c.helpful ? t("helpful") : t("notHelpful")}
                    </span>
                    <bdi>{c.comment}</bdi>
                  </li>
                ))}
              </ul>
            ) : null}
          </section>
        ) : null}
      </main>
    </>
  );
}
