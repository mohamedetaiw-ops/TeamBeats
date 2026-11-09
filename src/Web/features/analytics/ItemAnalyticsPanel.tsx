import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { noUnitGroup, otherGroup, type AnalyticsSource, type ItemAnalyticsView } from "@/lib/analytics/types";
import { RemindGroupButton } from "./RemindGroupButton";
import "./analytics.css";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/**
 * Reach and acknowledgement of one item (spec 021 US1): the totals, the breakdown by unit with small groups shown only
 * as "other", a reminder per group and the daily trend as a table. Everything shown comes from the API, which applies
 * the minimum group size and the owning module's rights.
 */
export async function ItemAnalyticsPanel({ source, view }: { source: AnalyticsSource; view: ItemAnalyticsView }) {
  const t = await getTranslations("analytics.item");
  const locale = await getLocale();
  const format = await getFormatter();
  const number = (n: number) => format.number(n);
  const percent = (n: number) => format.number(n, { style: "percent", maximumFractionDigits: 1 });
  const groupLabel = (group: string, names: Record<string, string>) =>
    group === otherGroup ? t("other", { size: view.minGroupSize }) : group === noUnitGroup ? t("noUnit") : pick(names, locale);
  const pending = (g: ItemAnalyticsView["groups"][number]) => (view.acknowledgementRequired ? g.audience - (g.acknowledged ?? 0) : g.audience - g.readers);

  return (
    <div className="analytics-item" data-testid="item-analytics">
      <section aria-labelledby="analytics-totals" className="analytics-card">
        <h2 id="analytics-totals">{t("totals")}</h2>
        <dl className="analytics-stats">
          <div>
            <dt>{t("audience")}</dt>
            <dd data-testid="analytics-audience">{number(view.audience)}</dd>
          </div>
          <div>
            <dt>{t("readers")}</dt>
            <dd data-testid="analytics-readers">{number(view.readers)}</dd>
          </div>
          <div>
            <dt>{t("reach")}</dt>
            <dd data-testid="analytics-reach">{percent(view.reach)}</dd>
          </div>
          {view.acknowledgementRequired && view.acknowledgedShare !== null ? (
            <div>
              <dt>{t("acknowledged")}</dt>
              <dd data-testid="analytics-acknowledged">{percent(view.acknowledgedShare)}</dd>
            </div>
          ) : null}
          {source === "content" ? (
            <>
              <div>
                <dt>{t("reactions")}</dt>
                <dd>{number(view.reactions)}</dd>
              </div>
              <div>
                <dt>{t("comments")}</dt>
                <dd>{number(view.comments)}</dd>
              </div>
            </>
          ) : null}
        </dl>
      </section>

      <section aria-labelledby="analytics-groups" className="analytics-card">
        <h2 id="analytics-groups">{t("groups")}</h2>
        <p className="analytics-meta">{t("privacy", { size: view.minGroupSize })}</p>
        {view.groups.length === 0 ? (
          <p className="analytics-meta">{t("noAudience")}</p>
        ) : (
          <div className="analytics-table">
            <table data-testid="analytics-groups">
              <thead>
                <tr>
                  <th scope="col">{t("column.group")}</th>
                  <th scope="col">{t("column.audience")}</th>
                  <th scope="col">{t("column.readers")}</th>
                  <th scope="col">{t("column.reach")}</th>
                  {view.acknowledgementRequired ? <th scope="col">{t("column.acknowledged")}</th> : null}
                  <th scope="col">
                    <span className="visually-hidden">{t("column.actions")}</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {view.groups.map((g) => {
                  const label = groupLabel(g.group, g.names);
                  return (
                    <tr key={g.group} data-group={g.group}>
                      <th scope="row">
                        <bdi>{label}</bdi>
                      </th>
                      <td data-number>{number(g.audience)}</td>
                      <td data-number>{number(g.readers)}</td>
                      <td data-number>{percent(g.reach)}</td>
                      {view.acknowledgementRequired ? <td data-number>{number(g.acknowledged ?? 0)}</td> : null}
                      <td>{pending(g) > 0 ? <RemindGroupButton source={source} itemId={view.itemId} group={g.group} label={label} /> : null}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section aria-labelledby="analytics-trend" className="analytics-card">
        <h2 id="analytics-trend">{t("trend")}</h2>
        {view.trend.length === 0 ? (
          <p className="analytics-meta">{t("noViews")}</p>
        ) : (
          <div className="analytics-table">
            <table data-testid="analytics-trend">
              <thead>
                <tr>
                  <th scope="col">{t("column.day")}</th>
                  <th scope="col">{t("column.views")}</th>
                  <th scope="col">{t("column.newReaders")}</th>
                </tr>
              </thead>
              <tbody>
                {view.trend.map((d) => (
                  <tr key={d.day}>
                    <th scope="row">{format.dateTime(new Date(`${d.day}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" })}</th>
                    <td data-number>{number(d.views)}</td>
                    <td data-number>{number(d.readers)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
