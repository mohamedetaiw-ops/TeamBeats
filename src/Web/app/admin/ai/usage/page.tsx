import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { DENIALS, known, USAGE_GROUPS, USE_CASES, type UsageRow, type UsageView } from "@/lib/ai/types";
import { Button } from "@/components/ui/Button";
import { AiTabs } from "@/features/ai/AiTabs";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

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
  const usage = await apiGet<UsageView>(`/api/v1/ai/usage?${query.toString()}`);
  const t = await getTranslations("ai");
  if (!usage.ok && usage.status !== 400) notFound();
  const locale = await getLocale();
  const format = await getFormatter();
  const n = (value: number) => format.number(value);
  const cost = (value: number) => format.number(value, { style: "currency", currency: "USD", maximumFractionDigits: 4 });

  function label(view: UsageView, row: UsageRow): string {
    if (row.key === null) return t("usage.none");
    switch (view.groupBy) {
      case "use_case": {
        const u = known(USE_CASES, row.key);
        return u ? t(`routing.useCases.${u}`) : row.key;
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
      <AdminHeader section="ai" title={t("usage.title")} nav={<AiTabs current="usage" />} />
      <form className="ai-section" method="get" aria-labelledby="ai-usage-filters">
        <h2 id="ai-usage-filters">{t("usage.filters")}</h2>
        <div className="ui-control-row">
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
          </section>

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
