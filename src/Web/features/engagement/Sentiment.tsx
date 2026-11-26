"use client";

import { useFormatter, useTranslations } from "next-intl";
import { ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";
import { StateView } from "@/components/ui/StateView";
import type { CommunitySentimentView } from "@/lib/engagement";
import "./engagement.css";

/**
 * A community's sentiment by week for its moderators and owners (spec 023 R3 US11 scenario 3, FR-033): the share of
 * positive, neutral and negative texts in each week the API gave counts for. Weeks below the minimum group size come
 * without counts and are left out; no text is ever shown.
 */
export function CommunitySentiment({ view }: { view: CommunitySentimentView }) {
  const t = useTranslations("engagement.sentiment");
  const format = useFormatter();
  if (!view.enabled) {
    return <StateView kind="empty" title={t("offTitle")} message={t("off")} icon="trend-up" />;
  }
  const shown = view.weeks.filter((w) => w.texts !== null && w.texts > 0);
  if (shown.length === 0) {
    return (
      <StateView
        kind="empty"
        title={view.weeks.length === 0 ? t("emptyTitle") : t("tooFewTitle")}
        message={view.weeks.length === 0 ? t("empty") : t("tooFew", { size: view.minGroupSize })}
        icon="trend-up"
      />
    );
  }
  const weeks = shown.map((w) => format.dateTime(new Date(`${w.week}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }));
  const share = (n: number | null, total: number | null) => (n !== null && total ? n / total : 0);
  const series = (["positive", "neutral", "negative"] as const).map((key) => ({
    key,
    label: t(key),
    values: shown.map((w) => share(w[key], w.texts)),
  }));
  const percent = (n: number) => format.number(n, { style: "percent", maximumFractionDigits: 0 });
  const hidden = view.weeks.length - shown.length;
  return (
    <div className="engagement">
      <ChartCard
        title={t("trend")}
        description={hidden > 0 ? t("hiddenWeeks", { count: hidden, size: view.minGroupSize }) : t("trendHint")}
        legend={series}
        table={seriesTable(t("week"), weeks, series, percent)}
      >
        <LineChart label={t("trendSummary", { weeks: shown.length })} categories={weeks} series={series} format={percent} />
      </ChartCard>
    </div>
  );
}
