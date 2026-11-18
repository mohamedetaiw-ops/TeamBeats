"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { complianceReportHref, noUnitGroup, otherGroup, type ComplianceAnalyticsView, type ComplianceCounts, type ComplianceGroup } from "@/lib/analytics/types";
import { ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";
import { ButtonLink } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { StateView } from "@/components/ui/StateView";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/**
 * Policy compliance analytics (spec 021 US5): each open campaign's attestation rate, overdue count and trend to the
 * deadline, by unit and location. Rows link to the spec 025 compliance view, which decides who sees the people.
 */
export function ComplianceDashboard({ view, locations }: { view: ComplianceAnalyticsView; locations: Readonly<Record<string, Record<string, string>>> }) {
  const t = useTranslations("analytics.compliance");
  const locale = useLocale();
  const format = useFormatter();
  const number = (n: number | null) => (n === null ? t("hidden", { size: view.minGroupSize }) : format.number(n));
  const rate = (n: number | null) => (n === null ? t("hidden", { size: view.minGroupSize }) : format.number(n, { style: "percent", maximumFractionDigits: 1 }));
  const day = (iso: string) => format.dateTime(new Date(`${iso.slice(0, 10)}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });

  if (view.campaigns.length === 0) {
    return <StateView kind="empty" title={t("empty")} message={t("emptyHint")} />;
  }

  const stats = (counts: ComplianceCounts, id: string) => (
    <dl className="analytics-stats" data-testid={id}>
      <div>
        <dt>{t("audience")}</dt>
        <dd>{format.number(counts.audience)}</dd>
      </div>
      <div>
        <dt>{t("rate")}</dt>
        <dd>{rate(counts.rate)}</dd>
      </div>
      <div>
        <dt>{t("attested")}</dt>
        <dd>{number(counts.attested)}</dd>
      </div>
      <div>
        <dt>{t("overdue")}</dt>
        <dd>{number(counts.overdue)}</dd>
      </div>
    </dl>
  );

  return (
    <div className="analytics-item" data-testid="compliance-dashboard">
      {stats(view.total, "compliance-total")}
      {view.campaigns.map((c) => {
        const name = pick(c.names, locale);
        const label = (dimension: "unit" | "location", g: ComplianceGroup) =>
          g.group === otherGroup
            ? t("other", { size: view.minGroupSize })
            : g.group === noUnitGroup
              ? t(dimension === "unit" ? "noUnit" : "noLocation")
              : dimension === "unit"
                ? pick(g.names, locale) || g.group
                : pick(locations[g.group] ?? {}, locale) || g.group;
        const drill = (dimension: "unit" | "location", g: ComplianceGroup) =>
          g.group === otherGroup || g.group === noUnitGroup
            ? null
            : complianceReportHref(c.policyId, c.campaignId, dimension === "unit" ? { unit: g.group } : { location: g.group });
        const series = [{ key: "rate", label: t("rate"), values: c.trend.map((p) => p.rate) }];
        const percent = (n: number) => format.number(n, { style: "percent", maximumFractionDigits: 1 });
        return (
          <section key={c.campaignId} className="analytics-card" aria-labelledby={`campaign-${c.campaignId}`} data-testid="compliance-campaign">
            <h2 id={`campaign-${c.campaignId}`}>
              <bdi>{name}</bdi> <Badge tone={c.daysLeft === 0 ? "danger" : "neutral"}>{t("daysLeft", { count: c.daysLeft })}</Badge>
            </h2>
            <p className="analytics-meta">{t("window", { launched: day(c.launchedAt), deadline: day(c.deadline), version: c.version })}</p>
            {stats(c.total, `compliance-campaign-${c.campaignId}`)}
            <p>
              <ButtonLink href={complianceReportHref(c.policyId, c.campaignId)} size="sm" icon="policy">
                {t("report")}
              </ButtonLink>
            </p>
            {c.trend.length > 1 ? (
              <ChartCard title={t("trend")} table={seriesTable(t("column.day"), c.trend.map((p) => day(p.day)), series, percent)}>
                <LineChart label={t("trendSummary", { name, rate: rate(c.total.rate) })} categories={c.trend.map((p) => day(p.day))} series={series} format={percent} area />
              </ChartCard>
            ) : null}
            {(["unit", "location"] as const).map((dimension) => {
              const rows = dimension === "unit" ? c.units : c.locations;
              return (
                <div key={dimension} className="analytics-table">
                  <table data-testid={`compliance-${dimension}s`}>
                    <caption>{t(dimension === "unit" ? "byUnit" : "byLocation")}</caption>
                    <thead>
                      <tr>
                        <th scope="col">{t(dimension === "unit" ? "column.unit" : "column.location")}</th>
                        <th scope="col">{t("audience")}</th>
                        <th scope="col">{t("rate")}</th>
                        <th scope="col">{t("overdue")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.length === 0 ? (
                        <tr>
                          <td colSpan={4}>{t("tooFew", { size: view.minGroupSize })}</td>
                        </tr>
                      ) : (
                        rows.map((g) => {
                          const href = drill(dimension, g);
                          return (
                            <tr key={g.group}>
                              <th scope="row">{href ? <a href={href}><bdi>{label(dimension, g)}</bdi></a> : <bdi>{label(dimension, g)}</bdi>}</th>
                              <td data-number>{format.number(g.counts.audience)}</td>
                              <td data-number>{rate(g.counts.rate)}</td>
                              <td data-number>{number(g.counts.overdue)}</td>
                            </tr>
                          );
                        })
                      )}
                    </tbody>
                  </table>
                </div>
              );
            })}
          </section>
        );
      })}
    </div>
  );
}
