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
import { PageHeader } from "@/components/ui/PageHeader";
import { TileCard } from "@/components/ui/TileCard";
import { Badge } from "@/components/ui/Badge";
import { ButtonLink } from "@/components/ui/Button";
import { FilterToolbar, ToolbarSearch, ToolbarSelect } from "@/components/ui/FilterToolbar";

const templates = ["sop", "howto", "faq", "troubleshooting"] as const;

/**
 * The knowledge base (spec 011 US4): published articles this person may read, the most recent first. The page opens
 * with search (spec 036 DLS-106): the box narrows the titles here and offers the search inside the articles.
 */
export default async function KnowledgeBasePage({ searchParams }: { searchParams: Promise<{ q?: string; template?: string }> }) {
  const params = await searchParams;
  const q = (params.q ?? "").trim().slice(0, 200);
  const template = templates.find((k) => k === params.template);
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
  const needle = q.toLocaleLowerCase(locale);
  const shown = articles.data.filter((a) => (!template || a.template === template) && (!needle || nameIn(a.titles, locale).toLocaleLowerCase(locale).includes(needle)));
  const href = (keep: { q?: string; template?: string }) => {
    const next = new URLSearchParams();
    if (keep.q) next.set("q", keep.q);
    if (keep.template) next.set("template", keep.template);
    return next.size > 0 ? `/kb?${next}` : "/kb";
  };
  const inside = q ? (
    <ButtonLink href={`/search?${new URLSearchParams({ q, kind: "knowledge" })}`} icon="search">
      {t("searchInside", { q })}
    </ButtonLink>
  ) : undefined;
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/kb" }]} />
      <main id="main" className="page">
        <PageHeader
          title={t("title")}
          description={t("intro")}
          icon="book"
          actions={
            <Link className="button" href="/kb/new">
              {t("new")}
            </Link>
          }
        />
        {articles.data.length > 0 ? (
          <FilterToolbar
            label={t("search")}
            method="get"
            search={<ToolbarSearch label={t("search")} name="q" placeholder={t("searchHint")} defaultValue={q} />}
            end={
              <>
                <button type="submit" className="button button-secondary">
                  {t("apply")}
                </button>
                {shown.length > 0 ? inside : null}
              </>
            }
            applied={[
              ...(q ? [{ key: "q", label: q, href: href({ template }) }] : []),
              ...(template ? [{ key: "template", label: t(`template.${template}`), href: href({ q }) }] : []),
            ]}
            clearHref="/kb"
          >
            <ToolbarSelect label={t("kind")} hideLabel name="template" defaultValue={template ?? ""}>
              <option value="">{t("anyKind")}</option>
              {templates.map((k) => (
                <option key={k} value={k}>
                  {t(`template.${k}`)}
                </option>
              ))}
            </ToolbarSelect>
          </FilterToolbar>
        ) : null}
        {articles.data.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : shown.length === 0 ? (
          <StateView kind="empty" icon="search" title={t("noMatch")} message={t("noMatchHint")} action={inside ?? <ButtonLink href="/kb">{t("clear")}</ButtonLink>} />
        ) : (
          <ul className="ui-tiles" data-testid="article-list">
            {shown.map((a) => (
              <li key={a.id}>
                <TileCard
                  title={nameIn(a.titles, locale)}
                  href={articleHref(a.id)}
                  icon="book"
                  meta={
                    <time dateTime={a.publishedAt}>
                      {t("listUpdated", { date: format.dateTime(new Date(a.publishedAt), { dateStyle: "medium" }) })}
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
