import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  articleHref,
  documentHref,
  nameIn,
  type ArticleView,
  type FeedbackReport,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { dateLong, joinMeta } from "@/lib/format";
import { ArticleFeedback } from "@/features/documents/ArticleFeedback";

/**
 * A knowledge article (spec 011 US4; spec 036 DLS-064): a reading column with the template, date, version and read time
 * in its header and Edit for editors, then its sections in the reader's language with anchors that citation links point at,
 * related articles and documents the reader can open, the helpfulness question, and for the owner and knowledge
 * editors the score and comments. On this page, Related and the score sit at the side; the helpfulness question
 * closes the article. An article with no text shows an empty state instead of a blank page. Articles the reader may not read are not found.
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
  const title = a.titles[a.locale] ?? nameIn(a.titles, locale);
  const words = a.sections.reduce((n, s) => n + s.text.split(/\s+/u).filter(Boolean).length, 0);
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
        <div className="kb-layout">
          <article lang={a.locale} className="documents-article kb-article">
            <header className="kb-article-head">
              <p className="kb-article-meta">
                {a.template ? (
                  <span className="ui-badge" data-tone="accent">
                    {t(`template.${a.template}`)}
                  </span>
                ) : null}
                <span>
                  {joinMeta([
                    t("updated", { date: dateLong(a.publishedAt, locale), version: a.version }),
                    words > 0 ? t("readTime", { minutes: Math.max(1, Math.round(words / 200)) }) : null,
                  ])}
                </span>
              </p>
              {a.canManage ? (
                <Link className="button button-secondary kb-edit" href={`/content/items/${encodeURIComponent(a.id)}`}>
                  <Icon name="edit" size={14} />
                  {t("edit")}
                </Link>
              ) : null}
            </header>
            <h1>
              <bdi>{title}</bdi>
            </h1>
            {words === 0 ? (
              <StateView
                kind="empty"
                icon="book"
                title={t("noBody")}
                message={a.canManage ? t("noBodyEditor") : t("noBodyReader")}
              />
            ) : (
              a.sections.map((s) => {
                const Tag = `h${Math.min(Math.max(s.level, 2), 4)}` as "h2" | "h3" | "h4";
                return (
                  <section key={s.anchor} aria-labelledby={s.anchor} className="documents-section">
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
              })
            )}
            <ArticleFeedback articleId={a.id} initial={a.myFeedback} />
          </article>

          <aside className="kb-aside">
            {a.sections.length > 1 ? (
              <nav aria-label={t("contents")} className="documents-toc kb-toc">
                <p className="kb-aside-title">{t("contents")}</p>
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

            {a.relatedArticles.length > 0 || a.relatedDocuments.length > 0 ? (
              <section className="documents-card kb-side-card" aria-label={t("related")}>
                <h2>{t("related")}</h2>
                <ul className="kb-related">
                  {a.relatedArticles.map((r) => (
                    <li key={r.id}>
                      <Link href={articleHref(r.id)}>
                        <Icon name="book" size={14} />
                        <bdi>{nameIn(r.titles, locale)}</bdi>
                      </Link>
                    </li>
                  ))}
                  {a.relatedDocuments.map((d) => (
                    <li key={d.id}>
                      <Link href={documentHref(d.id)}>
                        <Icon name="files" size={14} />
                        <bdi>{d.number ? `${d.number} ${d.name}` : d.name}</bdi>
                      </Link>
                    </li>
                  ))}
                </ul>
              </section>
            ) : null}

            {a.score ? (
              <section className="documents-card kb-side-card" aria-label={t("score")} data-testid="article-score">
                <h2>{t("score")}</h2>
                <p className="kb-side-text">
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
                        <span className="ui-badge" data-tone={c.helpful ? "success" : "warning"}>
                          {c.helpful ? t("helpful") : t("notHelpful")}
                        </span>
                        <bdi>{c.comment}</bdi>
                      </li>
                    ))}
                  </ul>
                ) : null}
              </section>
            ) : null}
          </aside>
        </div>
      </main>
    </>
  );
}
