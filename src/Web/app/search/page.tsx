import Link from "next/link";
import { redirect } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { isSearchKind, searchKinds, type SearchPage, type SearchResult } from "@/lib/search/types";
import "@/features/search/search.css";

type Params = { q?: string; kind?: string; page?: string };

function href(q: string, kind?: string, page?: number): string {
  const params = new URLSearchParams({ q });
  if (kind) params.set("kind", kind);
  if (page && page > 1) params.set("page", String(page));
  return `/search?${params.toString()}`;
}

/**
 * Search results (spec 012 US1, US4, T009): one ranked list across sources with filters by source, best bets marked as
 * recommended, the degraded note when meaning based search is unavailable, and services or people to try when nothing
 * matched. Every number comes from the API, which counts only what the person may open.
 */
export default async function SearchResultsPage({ searchParams }: { searchParams: Promise<Params> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const params = await searchParams;
  const q = (params.q ?? "").trim().slice(0, 200);
  const kind = isSearchKind(params.kind) ? params.kind : undefined;
  const number = Math.min(Math.max(Number.parseInt(params.page ?? "1", 10) || 1, 1), 50);
  const t = await getTranslations("search");
  const format = await getFormatter();

  const result = q
    ? await apiGet<SearchPage>(`/api/v1/search?${new URLSearchParams({ q, ...(kind ? { kind } : {}), page: String(number) }).toString()}`)
    : null;
  const page = result?.ok ? result.data : null;
  const pages = page ? Math.min(Math.ceil(page.total / page.pageSize), 50) : 0;
  const all = page ? Object.values(page.facets).reduce((sum, n) => sum + (n ?? 0), 0) : 0;

  const item = (r: SearchResult) => (
    <li key={`${r.source}:${r.itemId}`} className="search-result" lang={r.locale}>
      <div className="search-result-meta">
        <span className="search-kind">{t(`kinds.${r.kind}`)}</span>
        {r.recommended ? <span className="search-recommended">{t("recommended")}</span> : null}
        {r.updatedAt ? <time dateTime={r.updatedAt}>{format.dateTime(new Date(r.updatedAt), { dateStyle: "medium" })}</time> : null}
      </div>
      <h2 className="search-result-title">
        <Link href={r.link}>
          <bdi>{r.title}</bdi>
        </Link>
      </h2>
      {r.snippet ? <p className="search-snippet">{r.snippet}</p> : null}
    </li>
  );

  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/search" }]} />
      <main id="main" className="page search-page">
        <h1>{q ? t("titleFor", { q }) : t("title")}</h1>
        <form className="search-form" role="search" action="/search" method="get">
          <label htmlFor="search-q">{t("label")}</label>
          <div className="search-form-row">
            <input id="search-q" type="search" name="q" defaultValue={q} maxLength={200} required />
            {kind ? <input type="hidden" name="kind" value={kind} /> : null}
            <button type="submit" className="button">
              {t("submit")}
            </button>
          </div>
        </form>

        {result && !result.ok ? (
          <p className="notice" role="alert">
            {result.status === 403 ? t("notAvailable") : t("failed")}
          </p>
        ) : null}

        {page ? (
          <>
            <p className="search-count" role="status">
              {page.more ? t("countMore", { count: page.total }) : t("count", { count: page.total })}
            </p>
            {page.degraded ? <p className="notice">{t("degraded")}</p> : null}

            {all > 0 ? (
              <nav className="search-filters" aria-label={t("filters")}>
                <ul>
                  <li>
                    <Link href={href(q)} aria-current={kind ? undefined : "page"}>
                      {t("allKinds")} <span className="search-facet">{format.number(all)}</span>
                    </Link>
                  </li>
                  {searchKinds
                    .filter((k) => (page.facets[k] ?? 0) > 0)
                    .map((k) => (
                      <li key={k}>
                        <Link href={href(q, k)} aria-current={kind === k ? "page" : undefined}>
                          {t(`kinds.${k}`)} <span className="search-facet">{format.number(page.facets[k] ?? 0)}</span>
                        </Link>
                      </li>
                    ))}
                </ul>
              </nav>
            ) : null}

            {page.results.length > 0 ? <ol className="search-results">{page.results.map(item)}</ol> : null}

            {page.results.length === 0 ? (
              <div className="search-empty">
                <p>{t("none", { q })}</p>
                {page.alternatives.length > 0 ? (
                  <>
                    <h2>{t("alternatives")}</h2>
                    <ol className="search-results">{page.alternatives.map(item)}</ol>
                  </>
                ) : null}
              </div>
            ) : null}

            {pages > 1 ? (
              <nav className="search-pages" aria-label={t("pages")}>
                {number > 1 ? <Link href={href(q, kind, number - 1)}>{t("previous")}</Link> : null}
                <span>{t("pageOf", { page: number, pages })}</span>
                {number < pages ? <Link href={href(q, kind, number + 1)}>{t("next")}</Link> : null}
              </nav>
            ) : null}
          </>
        ) : null}
      </main>
    </>
  );
}
