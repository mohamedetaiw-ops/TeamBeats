"use client";

import { useLocale, useTranslations } from "next-intl";
import { BarChart, ChartCard, LineChart, seriesTable, type Series } from "@/components/ui/Charts";

/**
 * The visualization rules on the chart parts (spec 036 DLS-111): counts keep whole number ticks, and one data point
 * is shown as a number instead of a chart. Each chart keeps its unit, its comparison and its table.
 */
export function ChartRulesGallery() {
  const t = useTranslations("components_gallery.chartRules");
  const locale = useLocale();
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  const n = (v: number) => number.format(v);
  const teams = [t("teamIt"), t("teamPeople"), t("teamFinance")];
  const waiting: Series[] = [{ key: "waiting", label: t("waiting"), values: [1, 2, 1] }];
  const firstWeek: Series[] = [{ key: "active", label: t("active"), values: [128] }];
  return (
    <section aria-labelledby="cg-chart-rules">
      <h2 id="cg-chart-rules">{t("title")}</h2>
      <p className="notice">{t("intro")}</p>
      <div className="gallery-cards gallery-charts">
        <div data-testid="cg-chart-counts">
          <ChartCard title={t("countsTitle")} description={t("countsHint")} table={seriesTable(t("team"), teams, waiting, n)}>
            <BarChart label={t("countsSummary")} categories={teams} series={waiting} format={n} />
          </ChartCard>
        </div>
        <div data-testid="cg-chart-single">
          <ChartCard title={t("singleTitle")} description={t("singleHint")} table={seriesTable(t("week"), [t("weekOne")], firstWeek, n)}>
            <LineChart label={t("singleSummary")} categories={[t("weekOne")]} series={firstWeek} format={n} />
          </ChartCard>
        </div>
      </div>
    </section>
  );
}
