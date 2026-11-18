"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { noUnitGroup, otherGroup, serviceAnalyticsHref, type AnalyticsService, type ServiceAnalyticsView } from "@/lib/analytics/types";
import { ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";
import { DataTable } from "@/components/ui/DataTable";
import { ButtonLink } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { StateView } from "@/components/ui/StateView";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** The services the person owns (spec 021 US4). */
export function ServiceList({ services }: { services: readonly AnalyticsService[] }) {
  const t = useTranslations("analytics.services");
  const locale = useLocale();
  return (
    <DataTable
      caption={t("listCaption")}
      rows={services}
      rowKey={(s) => s.id}
      emptyTitle={t("empty")}
      emptyMessage={t("emptyHint")}
      columns={[
        {
          key: "name",
          header: t("column.service"),
          rowHeader: true,
          cell: (s) => <a href={serviceAnalyticsHref(s.id)}>{pick(s.names, locale)}</a>,
          sortValue: (s) => pick(s.names, locale),
        },
      ]}
    />
  );
}

/**
 * One service's analytics (spec 021 US4): requests per week, cycle time, rejections, breaches, automation, time per
 * step with slow steps marked and a link to the process heatmap when the API offers one, and structured reasons by unit.
 */
export function ServiceDashboard({ view }: { view: ServiceAnalyticsView }) {
  const t = useTranslations("analytics.services");
  const locale = useLocale();
  const format = useFormatter();
  const number = (n: number) => format.number(n);
  const hidden = t("hidden", { size: view.minGroupSize });
  const share = (n: number | null) => (n === null ? hidden : format.number(n, { style: "percent", maximumFractionDigits: 1 }));
  const duration = (seconds: number | null) => {
    if (seconds === null) return hidden;
    if (seconds < 3600) return t("minutes", { count: Math.round(seconds / 60) });
    if (seconds < 172800) return t("hours", { count: Math.round(seconds / 360) / 10 });
    return t("days", { count: Math.round(seconds / 8640) / 10 });
  };
  const week = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const weekSeries = [{ key: "requests", label: t("requests"), values: view.weeks.map((w) => w.requests) }];
  const group = (g: string, names: Record<string, string>) =>
    g === otherGroup ? t("other", { size: view.minGroupSize }) : g === noUnitGroup ? t("noUnit") : pick(names, locale) || g;

  if (view.requests === 0) {
    return <StateView kind="empty" title={t("noRequests")} message={t("noRequestsHint")} />;
  }

  return (
    <div className="analytics-item" data-testid="service-dashboard">
      <dl className="analytics-stats">
        <div>
          <dt>{t("requests")}</dt>
          <dd>{number(view.requests)}</dd>
        </div>
        <div>
          <dt>{t("median")}</dt>
          <dd data-testid="service-median">{duration(view.cycle.medianSeconds)}</dd>
        </div>
        <div>
          <dt>{t("p90")}</dt>
          <dd>{duration(view.cycle.p90Seconds)}</dd>
        </div>
        <div>
          <dt>{t("rejectedShare")}</dt>
          <dd>{share(view.rejectedShare)}</dd>
        </div>
        <div>
          <dt>{t("breachedShare")}</dt>
          <dd>{share(view.breachedShare)}</dd>
        </div>
        <div>
          <dt>{t("automationRate")}</dt>
          <dd>{share(view.automationRate)}</dd>
        </div>
      </dl>

      {view.weeks.length > 1 ? (
        <ChartCard title={t("weeks")} table={seriesTable(t("column.week"), view.weeks.map((w) => week(w.week)), weekSeries, number)}>
          <LineChart
            label={t("weeksSummary", { weeks: view.weeks.length, total: number(view.requests) })}
            categories={view.weeks.map((w) => week(w.week))}
            series={weekSeries}
            format={number}
            area
          />
        </ChartCard>
      ) : null}

      <section className="analytics-card" aria-labelledby="service-steps">
        <h2 id="service-steps">{t("steps")}</h2>
        {view.heatmapHref ? (
          <p>
            <ButtonLink href={view.heatmapHref} size="sm" icon="trend-up">
              {t("heatmap")}
            </ButtonLink>
          </p>
        ) : null}
        <div className="analytics-table">
          <table data-testid="service-steps">
            <thead>
              <tr>
                <th scope="col">{t("column.step")}</th>
                <th scope="col">{t("column.decisions")}</th>
                <th scope="col">{t("median")}</th>
                <th scope="col">{t("p90")}</th>
                <th scope="col">{t("column.automated")}</th>
                <th scope="col">{t("column.rejected")}</th>
              </tr>
            </thead>
            <tbody>
              {view.steps.map((s) => (
                <tr key={s.step}>
                  <th scope="row">
                    <bdi>{s.step}</bdi> {s.slow ? <Badge tone="warning">{t("slow")}</Badge> : null}
                  </th>
                  <td data-number>{number(s.decisions)}</td>
                  <td data-number>{duration(s.medianSeconds)}</td>
                  <td data-number>{duration(s.p90Seconds)}</td>
                  <td data-number>{number(s.automated)}</td>
                  <td data-number>{number(s.rejected)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {view.reasons.map((r) => (
        <section key={r.field} className="analytics-card" aria-labelledby={`reason-${r.field}`}>
          <h2 id={`reason-${r.field}`}>{t("reasonsTitle", { field: pick(r.labels, locale) })}</h2>
          <p className="analytics-meta">{t("reasonsNote", { size: view.minGroupSize })}</p>
          <div className="analytics-table">
            <table data-testid={`service-reasons-${r.field}`}>
              <thead>
                <tr>
                  <th scope="col">{t("column.unit")}</th>
                  <th scope="col">{t("column.total")}</th>
                  {r.options.map((o) => (
                    <th key={o.value} scope="col">
                      {pick(o.labels, locale)}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {[...r.rows, r.all].map((row) => (
                  <tr key={row.group}>
                    <th scope="row">
                      <bdi>{row.group === "all" ? t("allUnits") : group(row.group, row.names)}</bdi>
                    </th>
                    <td data-number>{number(row.total)}</td>
                    {r.options.map((o) => {
                      const n = row.counts[o.value];
                      return (
                        <td key={o.value} data-number>
                          {n === null || n === undefined ? hidden : number(n)}
                        </td>
                      );
                    })}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ))}
    </div>
  );
}
