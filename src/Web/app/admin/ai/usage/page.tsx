import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { BUDGET_SCOPES, CLASSIFICATIONS, DENIALS, known, USAGE_GROUPS, USE_CASES, type UsageForecast, type UsageRow, type UsageView } from "@/lib/ai/types";
import { Button, ButtonLink } from "@/components/ui/Button";
import { AiTabs } from "@/features/ai/AiTabs";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

type Filters = { from?: string; to?: string; groupBy?: string };

const DATE = /^\d{4}-\d{2}-\d{2}$/;

/**
 * AI usage (spec 013 US4, FR-013, T016): requests, tokens and estimated cost by day, use case, model, provider, org
 * unit, agent or person, and denied calls by reason. The API scopes the numbers to the viewer and hides small groups;
 * the bars are drawn from the same table, which is the accessible view of the chart.
 */
export default async function AiUsagePage({ searchParams }: { searchParams: Promise<Filters> }) {
  await aiSection();
  const filters = await searchParams;
  const query = new URLSearchParams();
  if (filters.from && DATE.test(filters.from)) query.set("from", filters.from);
  if (filters.to && DATE.test(filters.to)) query.set("to", filters.to);
  const groupBy = known(USAGE_GROUPS, filters.groupBy ?? "") ?? "day";
  query.set("groupBy", groupBy);
  const [usage, forecastResult] = await Promise.all([
    apiGet<UsageView>(`/api/v1/ai/usage?${query.toString()}`),
    apiGet<UsageForecast>("/api/v1/ai/usage/forecast"),
  ]);
  const t = await getTranslations("ai");
  if (!usage.ok && usage.status !== 400) notFound();
  const locale = await getLocale();
  const format = await getFormatter();
  const n = (value: number) => format.number(value);
  // A display currency set on the policy changes only what is shown; the API keeps every figure in US dollars.
  const money = (value: number, currency: string, rate: number) => format.number(value * rate, { style: "currency", currency, maximumFractionDigits: 4 });
  const cost = (value: number) => (usage.ok ? money(value, usage.data.currency, usage.data.rate) : money(value, "USD", 1));
  const forecast = forecastResult.ok ? forecastResult.data : null;
  const exportQuery = new URLSearchParams();
  if (query.get("from")) exportQuery.set("from", query.get("from") ?? "");
  if (query.get("to")) exportQuery.set("to", query.get("to") ?? "");

  function label(view: UsageView, row: UsageRow): string {
    if (row.key === null) return t("usage.none");
    switch (view.groupBy) {
      case "use_case": {
        const u = known(USE_CASES, row.key);
        return u ? t(`routing.useCases.${u}`) : row.key;
      }
      case "classification": {
        const level = known(CLASSIFICATIONS, row.key);
        return level ? t(`policy.classification.${level}`) : row.key;
      }
      case "day":
        return format.dateTime(new Date(`${row.key}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
      default: {
        const names = view.labels[row.key];
        return names?.[locale] ?? names?.en ?? names?.default ?? row.key;
      }
    }
  }

  const view = usage.ok ? usage.data : null;
  const peak = view ? Math.max(1, ...view.rows.map((r) => r.requests)) : 1;
  return (
    <>
      <h1>{t("usage.title")}</h1>
      <AiTabs current="usage" />
      <form className="ai-section" method="get" aria-labelledby="ai-usage-filters">
        <h2 id="ai-usage-filters">{t("usage.filters")}</h2>
        <div className="ai-row">
          <div className="field">
            <label htmlFor="ai-usage-from">{t("usage.from")}</label>
            <input id="ai-usage-from" name="from" type="date" defaultValue={view?.from ?? filters.from ?? ""} />
          </div>
          <div className="field">
            <label htmlFor="ai-usage-to">{t("usage.to")}</label>
            <input id="ai-usage-to" name="to" type="date" defaultValue={view?.to ?? filters.to ?? ""} />
          </div>
          <div className="field">
            <label htmlFor="ai-usage-group">{t("usage.groupBy")}</label>
            <select id="ai-usage-group" name="groupBy" defaultValue={groupBy}>
              {USAGE_GROUPS.filter((g) => g !== "user" || view?.scope !== "units").map((g) => (
                <option key={g} value={g}>
                  {t(`usage.groups.${g}`)}
                </option>
              ))}
            </select>
          </div>
          <Button type="submit">{t("usage.apply")}</Button>
        </div>
      </form>

      {!view ? (
        <p className="notice notice-error" role="alert">
          {t("usage.invalid")}
        </p>
      ) : (
        <>
          {view.scope === "units" ? <p className="notice">{t("usage.scoped", { size: String(view.minimumGroupSize) })}</p> : null}
          <section className="ai-section" aria-labelledby="ai-usage-totals">
            <h2 id="ai-usage-totals">{t("usage.totals")}</h2>
            <p data-testid="ai-usage-totals">
              {t("usage.totalsValue", {
                requests: n(view.totals.requests),
                tokens: n(view.totals.inputTokens + view.totals.outputTokens),
                cost: cost(view.totals.cost),
              })}
            </p>
            {view.currency !== "USD" ? <p className="field-hint">{t("usage.displayCurrency", { currency: view.currency, rate: n(view.rate) })}</p> : null}
            <div className="button-row">
              <ButtonLink variant="secondary" icon="download" href={`/api/v1/ai/usage/export?${exportQuery.toString()}`} download>
                {t("usage.export")}
              </ButtonLink>
            </div>
          </section>

          {forecast ? (
            <section className="ai-section" aria-labelledby="ai-usage-forecast" data-testid="ai-usage-forecast">
              <h2 id="ai-usage-forecast">{t("usage.forecast.title")}</h2>
              <p>
                {t("usage.forecast.summary", {
                  spent: money(forecast.monthToDate, forecast.currency, forecast.rate),
                  days: n(forecast.daysElapsed),
                  rate: money(forecast.dailyRate, forecast.currency, forecast.rate),
                  projected: money(forecast.projected, forecast.currency, forecast.rate),
                })}
              </p>
              {forecast.budgets.length > 0 ? (
                <div className="ai-table">
                  <table data-testid="ai-forecast-budgets">
                    <caption>{t("usage.forecast.budgets")}</caption>
                    <thead>
                      <tr>
                        <th scope="col">{t("usage.forecast.budget")}</th>
                        <th scope="col">{t("usage.forecast.amount")}</th>
                        <th scope="col">{t("usage.forecast.used")}</th>
                        <th scope="col">{t("usage.forecast.projected")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {forecast.budgets.map((b) => (
                        <tr key={b.id}>
                          <th scope="row">
                            {known(BUDGET_SCOPES, b.scope) ? t(`budgets.scopes.${known(BUDGET_SCOPES, b.scope)}`) : b.scope}
                          </th>
                          <td data-number="">{money(b.amount, forecast.currency, forecast.rate)}</td>
                          <td data-number="">{money(b.used, forecast.currency, forecast.rate)}</td>
                          <td data-number="">
                            {money(b.projected, forecast.currency, forecast.rate)}
                            {b.overBudget ? <span className="field-hint"> {t("usage.forecast.over")}</span> : null}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
            </section>
          ) : null}

          <section className="ai-section" aria-labelledby="ai-usage-rows">
            <h2 id="ai-usage-rows">{t("usage.byGroup", { group: t(`usage.groups.${groupBy}`) })}</h2>
            {view.rows.length === 0 ? (
              <p>{t("usage.empty")}</p>
            ) : (
              <div className="ai-table">
                <table data-testid="ai-usage-table">
                  <thead>
                    <tr>
                      <th scope="col">{t(`usage.groups.${groupBy}`)}</th>
                      <th scope="col">{t("usage.requests")}</th>
                      <th scope="col">{t("usage.inputTokens")}</th>
                      <th scope="col">{t("usage.outputTokens")}</th>
                      <th scope="col">{t("usage.cost")}</th>
                      <th scope="col">{t("usage.share")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {view.rows.map((r) => (
                      <tr key={r.key ?? ""}>
                        <th scope="row">
                          <bdi>{label(view, r)}</bdi>
                        </th>
                        <td data-number="">{n(r.requests)}</td>
                        <td data-number="">{n(r.inputTokens)}</td>
                        <td data-number="">{n(r.outputTokens)}</td>
                        <td data-number="">{cost(r.cost)}</td>
                        <td>
                          <span className="ai-bar" aria-hidden="true" style={{ inlineSize: `${Math.round((r.requests / peak) * 100)}%` }} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {view.hiddenGroups > 0 ? <p>{t("usage.hidden", { count: view.hiddenGroups, size: String(view.minimumGroupSize) })}</p> : null}
          </section>

          <section className="ai-section" aria-labelledby="ai-usage-denied">
            <h2 id="ai-usage-denied">{t("usage.denied")}</h2>
            {view.denied.length === 0 ? (
              <p>{t("usage.noDenied")}</p>
            ) : (
              <div className="ai-table">
                <table data-testid="ai-denied-table">
                  <thead>
                    <tr>
                      <th scope="col">{t("usage.reason")}</th>
                      <th scope="col">{t("usage.requests")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {view.denied.map((d) => {
                      const reason = known(DENIALS, d.reason);
                      return (
                        <tr key={d.reason}>
                          <th scope="row">{reason ? t(`denials.${reason}`) : d.reason}</th>
                          <td data-number="">{n(d.requests)}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </>
  );
}
