"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { otherGroup, type SearchGapsView, type SearchQueryRow } from "@/lib/analytics/types";
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
  const uiLocale = useLocale();
  const locale = view.locale ?? undefined;
  const href = (next: { locale?: string; unit?: string | null }) => {
    const params = new URLSearchParams();
    if (next.locale) params.set("locale", next.locale);
    if (next.unit) params.set("unit", next.unit);
    const query = params.toString();
    return query ? `/analytics/search?${query}` : "/analytics/search";
  };
  const pick = (value: string) => router.push(href({ locale: value === anyLocale ? undefined : value, unit: view.unitId }));
  const named = (names: Record<string, string> | null) => (names ? (names[uiLocale] ?? names.en ?? Object.values(names)[0] ?? "") : "");
  const count = (n: number | null) => (n === null ? t("units.hidden") : format.number(n));

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
      {view.unitId ? (
        <Alert tone="info" title={t("unitFilter", { unit: named(view.unitNames) })}>
          <p>
            <Link href={href({ locale })}>{t("allUnits")}</Link>
          </p>
        </Alert>
      ) : null}
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
            <div>
              <dt>{t("refined")}</dt>
              <dd>{format.number(view.refined)}</dd>
            </div>
          </dl>
          {!view.unitId && view.units.length > 0 ? (
            <section className="analytics-card" aria-labelledby="search-units">
              <h2 id="search-units">{t("units.title")}</h2>
              <p className="analytics-note">{t("units.hint")}</p>
              <div className="analytics-table">
                <table data-testid="search-units">
                  <thead>
                    <tr>
                      <th scope="col">{t("column.unit")}</th>
                      <th scope="col">{t("column.searches")}</th>
                      <th scope="col">{t("column.people")}</th>
                      <th scope="col">{t("column.zeroResults")}</th>
                      <th scope="col">{t("column.clicks")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {view.units.map((u) => (
                      <tr key={u.key}>
                        <th scope="row">
                          {u.key === otherGroup ? (
                            t("units.other", { size: view.minPeople })
                          ) : u.key === "none" ? (
                            t("units.none")
                          ) : (
                            <Link href={href({ locale, unit: u.key })}>
                              <bdi>{named(u.names) || u.key}</bdi>
                            </Link>
                          )}
                        </th>
                        <td data-number>{count(u.searches)}</td>
                        <td data-number>{u.key === otherGroup && u.searches !== null ? t("units.notCounted") : count(u.people)}</td>
                        <td data-number>{count(u.zeroResults)}</td>
                        <td data-number>{count(u.clicks)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          ) : null}
          {view.queryTextHidden ? (
            <Alert tone="info" title={t("textHidden")}>
              <p>{t("textHiddenHint")}</p>
            </Alert>
          ) : (
            <>
              {table("zero", view.zeroResultQueries, view.canAddBestBet || view.canWriteArticle)}
              {table("noClick", view.noClickQueries, false)}
              {table("top", view.topQueries, false)}
              <section className="analytics-card" aria-labelledby="search-refinements">
                <h2 id="search-refinements">{t("refinements.title")}</h2>
                <p className="analytics-note">{t("refinements.hint")}</p>
                {view.refinements.length === 0 ? (
                  <p>{t("refinements.empty", { size: view.minPeople })}</p>
                ) : (
                  <div className="analytics-table">
                    <table data-testid="search-refinements">
                      <thead>
                        <tr>
                          <th scope="col">{t("column.from")}</th>
                          <th scope="col">{t("column.to")}</th>
                          <th scope="col">{t("column.searches")}</th>
                          <th scope="col">{t("column.people")}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {view.refinements.map((r) => (
                          <tr key={JSON.stringify([r.from, r.to])}>
                            <th scope="row">
                              <bdi>{r.from}</bdi>
                            </th>
                            <td>
                              <bdi>{r.to}</bdi>
                            </td>
                            <td data-number>{format.number(r.searches)}</td>
                            <td data-number>{format.number(r.people)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </section>
            </>
          )}
          <p className="analytics-note">{t("privacyNote", { size: view.minPeople })}</p>
        </>
      )}
    </div>
  );
}
