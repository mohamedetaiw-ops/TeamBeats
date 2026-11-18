"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { noUnitGroup, otherGroup, type AnalyticsPolicyView, type SentimentCounts, type SentimentGroup, type SentimentView } from "@/lib/analytics/types";
import { problemMessages } from "@/lib/notifications/types";
import { ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";
import { Switch } from "@/components/ui/Switch";
import { Alert } from "@/components/ui/Alert";
import { StateView } from "@/components/ui/StateView";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** The tenant's sentiment switch (spec 021 FR-013), for people who may change the analytics policy. */
export function SentimentSwitch({ policy }: { policy: AnalyticsPolicyView }) {
  const t = useTranslations("analytics.sentiment");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [on, setOn] = useState(policy.sentiment);

  async function change(next: boolean) {
    setBusy(true);
    setError(null);
    setOn(next);
    try {
      const response = await fetch("/api/v1/analytics/policy", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ sentiment: next, version: policy.version }),
      });
      if (!response.ok) {
        setOn(!next);
        setError((await problemMessages(response))[0] ?? t("switchFailed"));
        return;
      }
      router.refresh();
    } catch {
      setOn(!next);
      setError(t("switchFailed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="analytics-card" aria-labelledby="sentiment-setting">
      <h2 id="sentiment-setting">{t("setting")}</h2>
      <Switch label={t("switch")} hint={t("switchHint")} checked={on} disabled={busy} onChange={(e) => void change(e.target.checked)} />
      {error ? <Alert tone="danger" title={error} live /> : null}
    </section>
  );
}

/**
 * Sentiment of comments and survey free text (spec 021 US8): labels by week, topic, unit, location and source, each
 * shown only with at least the minimum group size of people behind it. No text and no person is ever shown.
 */
export function SentimentDashboard({ view, locations }: { view: SentimentView; locations: Readonly<Record<string, Record<string, string>>> }) {
  const t = useTranslations("analytics.sentiment");
  const locale = useLocale();
  const format = useFormatter();
  const hidden = t("hidden", { size: view.minGroupSize });
  const number = (n: number | null) => (n === null ? hidden : format.number(n));
  const score = (n: number | null) => (n === null ? hidden : format.number(n, { maximumFractionDigits: 2, signDisplay: "exceptZero" }));
  const week = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const label = (dimension: "topic" | "unit" | "location" | "source", g: SentimentGroup) => {
    if (g.group === otherGroup) return t("other", { size: view.minGroupSize });
    if (g.group === noUnitGroup) return t(dimension === "unit" ? "noUnit" : "notRecorded");
    if (dimension === "unit") return pick(g.names, locale) || g.group;
    if (dimension === "location") return pick(locations[g.group] ?? {}, locale) || g.group;
    if (dimension === "source") return t.has(`source.${g.group}`) ? t(`source.${g.group}`) : g.group;
    return g.group;
  };
  const cells = (c: SentimentCounts) => (
    <>
      <td data-number>{number(c.texts)}</td>
      <td data-number>{number(c.positive)}</td>
      <td data-number>{number(c.neutral)}</td>
      <td data-number>{number(c.negative)}</td>
      <td data-number>{score(c.score)}</td>
    </>
  );

  const shownWeeks = view.weeks.filter((w) => w.counts.score !== null);
  const series = [{ key: "score", label: t("score"), values: shownWeeks.map((w) => w.counts.score ?? 0) }];
  const scoreFormat = (n: number) => format.number(n, { maximumFractionDigits: 2, signDisplay: "exceptZero" });

  return (
    <div className="analytics-item" data-testid="sentiment-dashboard">
      {view.paused.map((p) => (
        <Alert key={p.source} tone="warning" title={t("paused", { source: t.has(`source.${p.source}`) ? t(`source.${p.source}`) : p.source })}>
          <p>{t.has(`denial.${p.denial}`) ? t(`denial.${p.denial}`) : t("denial.other")}</p>
        </Alert>
      ))}
      {!view.enabled ? (
        <StateView kind="empty" title={t("off")} message={t("offHint")} />
      ) : view.total.texts === 0 ? (
        <StateView kind="empty" title={t("empty")} message={t("emptyHint")} />
      ) : view.total.score === null ? (
        <StateView kind="empty" title={t("tooFew")} message={t("tooFewHint", { size: view.minGroupSize })} />
      ) : (
        <>
          <dl className="analytics-stats" data-testid="sentiment-total">
            <div>
              <dt>{t("texts")}</dt>
              <dd>{number(view.total.texts)}</dd>
            </div>
            <div>
              <dt>{t("positive")}</dt>
              <dd>{number(view.total.positive)}</dd>
            </div>
            <div>
              <dt>{t("neutral")}</dt>
              <dd>{number(view.total.neutral)}</dd>
            </div>
            <div>
              <dt>{t("negative")}</dt>
              <dd>{number(view.total.negative)}</dd>
            </div>
            <div>
              <dt>{t("score")}</dt>
              <dd>{score(view.total.score)}</dd>
            </div>
          </dl>
          <p className="analytics-note">{t("scoreHint")}</p>
          {shownWeeks.length > 1 ? (
            <ChartCard title={t("trend")} table={seriesTable(t("column.week"), shownWeeks.map((w) => week(w.week)), series, scoreFormat)}>
              <LineChart label={t("trendSummary", { weeks: shownWeeks.length })} categories={shownWeeks.map((w) => week(w.week))} series={series} format={scoreFormat} />
            </ChartCard>
          ) : null}
          {(["topic", "unit", "location", "source"] as const).map((dimension) => {
            const rows = dimension === "topic" ? view.topics : dimension === "unit" ? view.units : dimension === "location" ? view.locations : view.sources;
            return (
              <section key={dimension} className="analytics-card" aria-labelledby={`sentiment-${dimension}`}>
                <h2 id={`sentiment-${dimension}`}>{t(`by.${dimension}`)}</h2>
                <div className="analytics-table">
                  <table data-testid={`sentiment-${dimension}s`}>
                    <thead>
                      <tr>
                        <th scope="col">{t(`column.${dimension}`)}</th>
                        <th scope="col">{t("texts")}</th>
                        <th scope="col">{t("positive")}</th>
                        <th scope="col">{t("neutral")}</th>
                        <th scope="col">{t("negative")}</th>
                        <th scope="col">{t("score")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((g) => (
                        <tr key={g.group}>
                          <th scope="row">
                            <bdi>{label(dimension, g)}</bdi>
                          </th>
                          {cells(g.counts)}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </section>
            );
          })}
          <p className="analytics-note">{t("privacyNote", { size: view.minGroupSize })}</p>
        </>
      )}
    </div>
  );
}
