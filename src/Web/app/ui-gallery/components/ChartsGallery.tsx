"use client";

import { useLocale, useTranslations } from "next-intl";
import {
  BarChart,
  BarList,
  ChartCard,
  Contributions,
  FlowChart,
  FunnelChart,
  HeatMap,
  LineChart,
  RadarChart,
  RadialChart,
  Rings,
  ScatterChart,
  StageBars,
  seriesTable,
  type Series,
} from "@/components/ui/Charts";

const months = ["2026-06-01", "2026-07-01", "2026-08-01", "2026-09-01", "2026-10-01", "2026-11-01"];
/** A fixed pattern of daily activity, so the baselines do not change. */
const activity = Array.from({ length: 18 * 7 }, (_, i) => ((i * 7 + (i % 5) * 3) % 11) - (i % 7 === 5 || i % 7 === 6 ? 9 : 0)).map((v) => Math.max(0, v));

/** Charts of the extended catalogue (spec 036 DLS-040), on workplace analytics. */
export function ChartsGallery() {
  const t = useTranslations("components_gallery.charts");
  const locale = useLocale();
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 });
  const compact = new Intl.NumberFormat(locale, { notation: "compact", maximumFractionDigits: 1 });
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 0 });
  const monthName = new Intl.DateTimeFormat(locale, { month: "short", timeZone: "UTC" });
  const categories = months.map((m) => monthName.format(Date.parse(m)));
  const n = (v: number) => number.format(v);
  const c = (v: number) => compact.format(v);

  const visits: Series[] = [
    { key: "web", label: t("web"), values: [4200, 4800, 4500, 5600, 6100, 6900] },
    { key: "mobile", label: t("mobile"), values: [1800, 2300, 2900, 3400, 4200, 5100] },
  ];
  const requests: Series[] = [
    { key: "it", label: t("it"), values: [320, 290, 350, 410, 380, 360] },
    { key: "hr", label: t("hr"), values: [210, 240, 230, 260, 300, 280] },
    { key: "finance", label: t("finance"), values: [120, 140, 160, 150, 170, 190] },
  ];
  const target: Series = { key: "target", label: t("target"), values: [600, 620, 650, 700, 720, 750] };
  const headcount: Series[] = [{ key: "people", label: t("people"), values: [410, 410, 425, 425, 448, 452] }];
  const skills: Series[] = [
    { key: "team", label: t("team"), values: [4, 3, 5, 2, 4] },
    { key: "company", label: t("company"), values: [3, 4, 3, 3, 3] },
  ];
  const axes = [t("skillData"), t("skillDesign"), t("skillWriting"), t("skillAi"), t("skillLeading")];
  const days = [t("sun"), t("mon"), t("tue"), t("wed"), t("thu")];
  const hours = ["09", "11", "13", "15", "17"];
  const heat = [
    [12, 30, 22, 18, 6],
    [25, 48, 35, 28, 10],
    [20, 44, 40, 31, 12],
    [18, 39, 36, 25, 9],
    [9, 20, 15, 11, 4],
  ];
  const funnel = [
    { key: "invited", label: t("invited"), value: 1200 },
    { key: "opened", label: t("opened"), value: 940 },
    { key: "started", label: t("started"), value: 610 },
    { key: "completed", label: t("completed"), value: 470 },
  ];
  const stages = [
    { key: "new", label: t("stageNew"), value: 42 },
    { key: "progress", label: t("stageProgress"), value: 65 },
    { key: "waiting", label: t("stageWaiting"), value: 18 },
    { key: "done", label: t("stageDone"), value: 120 },
  ];
  const sources = [
    { key: "search", label: t("fromSearch") },
    { key: "home", label: t("fromHome") },
    { key: "assistant", label: t("fromAssistant") },
  ];
  const targets = [
    { key: "policies", label: t("toPolicies") },
    { key: "services", label: t("toServices") },
    { key: "people", label: t("toPeople") },
  ];
  const links = [
    { from: "search", to: "policies", value: 420 },
    { from: "search", to: "people", value: 380 },
    { from: "home", to: "services", value: 510 },
    { from: "home", to: "policies", value: 160 },
    { from: "assistant", to: "policies", value: 300 },
    { from: "assistant", to: "services", value: 220 },
  ];
  const scatter = [
    { key: "teams", label: t("teams"), points: [{ x: 12, y: 78 }, { x: 25, y: 84 }, { x: 8, y: 65 }, { x: 31, y: 90 }, { x: 18, y: 72 }] },
    { key: "offices", label: t("offices"), points: [{ x: 40, y: 70 }, { x: 55, y: 81 }, { x: 47, y: 62 }] },
  ];
  const rings = [
    { key: "learning", label: t("ringLearning"), value: 34, max: 40, valueText: t("ringLearningValue") },
    { key: "wellbeing", label: t("ringWellbeing"), value: 6, max: 10, valueText: t("ringWellbeingValue") },
    { key: "recognition", label: t("ringRecognition"), value: 9, max: 12, valueText: t("ringRecognitionValue") },
  ];

  return (
    <>
      <section aria-labelledby="cg-charts">
        <h2 id="cg-charts">{t("title")}</h2>
        <p className="notice">{t("intro")}</p>
        <div className="gallery-cards gallery-charts">
          <ChartCard title={t("visits")} description={t("visitsHint")} figure={n(12000)} legend={visits} table={seriesTable(t("month"), categories, visits, n)}>
            <LineChart label={t("visitsSummary")} categories={categories} series={visits} format={c} area stacked />
          </ChartCard>
          <ChartCard title={t("requests")} description={t("requestsHint")} legend={[...requests, target]} table={seriesTable(t("month"), categories, [...requests, target], n)}>
            <BarChart label={t("requestsSummary")} categories={categories} series={requests} line={target} format={c} stacked />
          </ChartCard>
          <ChartCard title={t("trend")} legend={requests} table={seriesTable(t("month"), categories, requests, n)}>
            <BarChart label={t("trendSummary")} categories={categories} series={requests} format={c} />
          </ChartCard>
          <ChartCard title={t("headcount")} description={t("headcountHint")} table={seriesTable(t("month"), categories, headcount, n)}>
            <LineChart label={t("headcountSummary")} categories={categories} series={headcount} format={n} curve="step" />
          </ChartCard>
          <ChartCard title={t("topSearches")} table={{ head: [t("term"), t("searches")], rows: [[t("termLeave"), n(1840)], [t("termPayslip"), n(1320)], [t("termParking"), n(860)], [t("termVpn"), n(540)]] }}>
            <BarList
              label={t("topSearches")}
              items={[
                { key: "leave", label: t("termLeave"), value: 1840, valueText: n(1840) },
                { key: "payslip", label: t("termPayslip"), value: 1320, valueText: n(1320) },
                { key: "parking", label: t("termParking"), value: 860, valueText: n(860) },
                { key: "vpn", label: t("termVpn"), value: 540, valueText: n(540) },
              ]}
            />
          </ChartCard>
          <ChartCard title={t("survey")} description={t("surveyHint")} table={{ head: [t("stage"), t("people")], rows: funnel.map((f) => [f.label, n(f.value)]) }}>
            <FunnelChart label={t("surveySummary")} stages={funnel} format={n} />
          </ChartCard>
        </div>
      </section>

      <section aria-labelledby="cg-charts-more">
        <h2 id="cg-charts-more">{t("moreTitle")}</h2>
        <p className="notice">{t("moreIntro")}</p>
        <div className="gallery-cards gallery-charts">
          <ChartCard title={t("busy")} description={t("busyHint")} table={{ head: [t("day"), ...hours], rows: days.map((d, i) => [d, ...(heat[i] ?? []).map(n)]) }}>
            <HeatMap label={t("busySummary")} rows={days} columns={hours} values={heat} format={n} />
          </ChartCard>
          <ChartCard title={t("skills")} legend={skills} table={seriesTable(t("skill"), axes, skills, n)}>
            <RadarChart label={t("skillsSummary")} axes={axes} series={skills} max={5} />
          </ChartCard>
          <ChartCard title={t("adoption")} table={{ head: [t("measure"), t("value")], rows: [[t("adoption"), percent.format(0.72)]] }}>
            <RadialChart label={t("adoptionSummary")} value={72} max={100} valueText={percent.format(0.72)} caption={t("adoptionCaption")} />
          </ChartCard>
          <ChartCard title={t("goals")} table={{ head: [t("goal"), t("value")], rows: rings.map((r) => [r.label, r.valueText]) }}>
            <Rings label={t("goalsSummary")} rings={rings} />
          </ChartCard>
          <ChartCard title={t("journeys")} description={t("journeysHint")} table={{ head: [t("from"), t("to"), t("visitsShort")], rows: links.map((l) => [sources.find((s) => s.key === l.from)!.label, targets.find((x) => x.key === l.to)!.label, n(l.value)]) }}>
            <FlowChart label={t("journeysSummary")} sources={sources} targets={targets} links={links} format={c} />
          </ChartCard>
          <ChartCard
            title={t("engagement")}
            description={t("engagementHint")}
            legend={scatter}
            table={{ head: [t("group"), t("size"), t("score")], rows: scatter.flatMap((s) => s.points.map((p) => [s.label, n(p.x), n(p.y)])) }}
          >
            <ScatterChart label={t("engagementSummary")} series={scatter} formatX={n} formatY={n} />
          </ChartCard>
          <ChartCard title={t("contributions")} description={t("contributionsHint")} table={{ head: [t("measure"), t("value")], rows: [[t("contributionsTotal"), n(activity.reduce((a, b) => a + b, 0))]] }}>
            <Contributions label={t("contributionsSummary")} start="2026-07-05" values={activity} />
          </ChartCard>
          <ChartCard title={t("pipeline")} table={{ head: [t("stage"), t("requestsShort")], rows: stages.map((s) => [s.label, n(s.value)]) }}>
            <StageBars label={t("pipelineSummary")} stages={stages} format={n} />
          </ChartCard>
        </div>
      </section>
    </>
  );
}
