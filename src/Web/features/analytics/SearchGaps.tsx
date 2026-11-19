"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import type { SearchGapsView, SearchQueryRow } from "@/lib/analytics/types";
import { ButtonLink } from "@/components/ui/Button";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { Alert } from "@/components/ui/Alert";

const anyLocale = "any";

/**
 * Search gaps (spec 021 US3, FR-007): what people search for and do not find, by locale, from the spec 012 query feed.
 * A query appears only when at least five people searched for it, never when it looks like personal data. Zero result
 * queries lead to a best bet or a new knowledge article for the people who may add them; those pages check again.
 */
export function SearchGapsDashboard({ view, locales }: { view: SearchGapsView; locales: string[] }) {
  const t = useTranslations("analytics.search");
  const format = useFormatter();
  const router = useRouter();
  const names = new Intl.DisplayNames([useLocale()], { type: "language" });
  const pick = (value: string) => router.push(value === anyLocale ? "/analytics/search" : `/analytics/search?locale=${encodeURIComponent(value)}`);
  const locale = view.locale ?? undefined;

  const table = (key: "zero" | "noClick" | "top", rows: SearchQueryRow[], actions: boolean) => (
    <section className="analytics-card" aria-labelledby={`search-${key}`}>
      <h2 id={`search-${key}`}>{t(`${key}.title`)}</h2>
      <p className="analytics-note">{t(`${key}.hint`)}</p>
      {rows.length === 0 ? (
        <p>{t("noQueries", { size: view.minPeople })}</p>
      ) : (
        <div className="analytics-table">
          <table data-testid={`search-${key}`}>
            <thead>
              <tr>
                <th scope="col">{t("column.query")}</th>
                <th scope="col">{t("column.searches")}</th>
                <th scope="col">{t("column.people")}</th>
                <th scope="col">{key === "zero" ? t("column.zeroResults") : t("column.clicks")}</th>
                {actions ? <th scope="col">{t("column.actions")}</th> : null}
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.query}>
                  <th scope="row">
                    <bdi>{r.query}</bdi>
                  </th>
                  <td data-number>{format.number(r.searches)}</td>
                  <td data-number>{format.number(r.people)}</td>
                  <td data-number>{format.number(key === "zero" ? r.zeroResults : r.clicks)}</td>
                  {actions ? (
                    <td>
                      <div className="analytics-report-files">
                        {view.canAddBestBet ? (
                          <ButtonLink
                            size="sm"
                            icon="star"
                            href={`/admin/search?query=${encodeURIComponent(r.query)}${locale ? `&locale=${locale}` : ""}#best-bets`}
                            aria-label={t("bestBetFor", { query: r.query })}
                          >
                            {t("bestBet")}
                          </ButtonLink>
                        ) : null}
                        {view.canWriteArticle ? (
                          <ButtonLink
                            size="sm"
                            icon="book"
                            href={`/kb/new?title=${encodeURIComponent(r.query)}${locale ? `&locale=${locale}` : ""}`}
                            aria-label={t("articleFor", { query: r.query })}
                          >
                            {t("article")}
                          </ButtonLink>
                        ) : null}
                      </div>
                    </td>
                  ) : null}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );

  return (
    <div className="analytics-item" data-testid="search-gaps">
      <SegmentedControl
        label={t("locale")}
        name="locale"
        value={view.locale ?? anyLocale}
        onChange={pick}
        options={[{ value: anyLocale, label: t("anyLocale") }, ...locales.map((l) => ({ value: l, label: names.of(l) ?? l }))]}
      />
      {view.searches === 0 ? (
        <StateView kind="empty" title={t("empty")} message={t("emptyHint")} />
      ) : (
        <>
          <dl className="analytics-stats" data-testid="search-total">
            <div>
              <dt>{t("searches")}</dt>
              <dd>{format.number(view.searches)}</dd>
            </div>
            <div>
              <dt>{t("zeroResults")}</dt>
              <dd>{format.number(view.zeroResults)}</dd>
            </div>
            <div>
              <dt>{t("clickThrough")}</dt>
              <dd>{format.number(view.clickThrough, { style: "percent", maximumFractionDigits: 1 })}</dd>
            </div>
          </dl>
          {view.queryTextHidden ? (
            <Alert tone="info" title={t("textHidden")}>
              <p>{t("textHiddenHint")}</p>
            </Alert>
          ) : (
            <>
              {table("zero", view.zeroResultQueries, view.canAddBestBet || view.canWriteArticle)}
              {table("noClick", view.noClickQueries, false)}
              {table("top", view.topQueries, false)}
            </>
          )}
          <p className="analytics-note">{t("privacyNote", { size: view.minPeople })}</p>
        </>
      )}
    </div>
  );
}
