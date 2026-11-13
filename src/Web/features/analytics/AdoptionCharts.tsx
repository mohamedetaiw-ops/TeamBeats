"use client";

import { useFormatter, useTranslations } from "next-intl";
import { BarChart, ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";

/** How many weeks the trend shows: the current week and the four before it (spec 021 FR-025). */
export const TREND_WEEKS = 5;

/**
 * Adoption as charts (spec 021 FR-025, TB-16): weekly active people as a line and active people by unit as bars, each
 * with its table alternative and a sentence for screen readers. The API has already merged units under the minimum
 * group size into "Other"; the note under the bars says so. Labels arrive written; this only draws.
 */
export function AdoptionCharts({
  weeks,
  units,
  unitsTitle,
  minGroupSize,
}: {
  weeks: readonly { label: string; value: number }[];
  units: readonly { key: string; label: string; value: number }[];
  unitsTitle: string;
  minGroupSize: number;
}) {
  const t = useTranslations("analytics.adoption");
  const format = useFormatter();
  const number = (n: number) => format.number(n);
  const trend = weeks.slice(-TREND_WEEKS);
  const weekSeries = [{ key: "active", label: t("column.active"), values: trend.map((w) => w.value) }];
  const unitSeries = [{ key: "active", label: t("column.active"), values: units.map((u) => u.value) }];
  const last = trend.at(-1)?.value;
  return (
    <div className="analytics-charts">
      {trend.length > 0 ? (
        <div data-testid="adoption-weeks-chart">
          <ChartCard
            title={t("weeks")}
            figure={last !== undefined ? <strong className="analytics-figure">{number(last)}</strong> : undefined}
            table={seriesTable(t("column.week"), trend.map((w) => w.label), weekSeries, number)}
          >
            <LineChart
              label={t("weeksSummary", { weeks: trend.length, last: number(last ?? 0) })}
              categories={trend.map((w) => w.label)}
              series={weekSeries}
              format={number}
              area
            />
          </ChartCard>
        </div>
      ) : null}
      {units.length > 0 ? (
        <div data-testid="adoption-units-chart">
          <ChartCard
            title={unitsTitle}
            description={t("otherNote", { size: minGroupSize })}
            table={seriesTable(t("column.unit"), units.map((u) => u.label), unitSeries, number)}
          >
            <BarChart label={t("unitsSummary", { count: units.length })} categories={units.map((u) => u.label)} series={unitSeries} format={number} />
          </ChartCard>
        </div>
      ) : null}
    </div>
  );
}
