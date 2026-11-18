"use client";

import { useEffect, useMemo, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { analyticsQueryApi, noUnitGroup, otherGroup, type MetricFilters, type MetricResult, type WorkspaceView } from "@/lib/analytics/types";
import { announceReportDefinition } from "@/lib/analytics/reports";
import { problemMessages } from "@/lib/notifications/types";
import { BarChart, ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";
import { FilterToolbar, ToolbarSelect, type AppliedFilter } from "@/components/ui/FilterToolbar";
import { StateView } from "@/components/ui/StateView";
import { Skeleton } from "@/components/ui/Skeleton";

/** The periods offered, in weeks: the current week and the ones before it. The API caps a range at 400 days. */
const PERIODS = [5, 13, 52] as const;

type Answer = { key: string; result?: MetricResult; messages?: string[] };

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** The first day of the week `weeks - 1` weeks before this one, as the API's weeks start on Monday (UTC). */
function periodStart(weeks: number): string {
  const now = new Date();
  const day = (now.getUTCDay() + 6) % 7;
  const start = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - day - 7 * (weeks - 1)));
  return start.toISOString().slice(0, 10);
}

/**
 * The workspace explorer (spec 021 T010): pick a metric, a breakdown and filters, and see the weekly trend and the
 * breakdown as charts with table alternatives. Every rule (scope, minimum group size, which filters a metric takes)
 * is the API's; this only asks and draws, and shows a suppressed selection as such.
 */
export function WorkspaceExplorer({ workspace }: { workspace: WorkspaceView }) {
  const t = useTranslations("analytics.workspace");
  const locale = useLocale();
  const format = useFormatter();
  const number = (n: number) => format.number(n);
  const [metric, setMetric] = useState(workspace.metrics[0]?.key ?? "");
  const [groupBy, setGroupBy] = useState("");
  const [unitId, setUnitId] = useState("");
  const [membershipType, setMembershipType] = useState("");
  const [weeks, setWeeks] = useState<number>(PERIODS[0]);
  const [answer, setAnswer] = useState<Answer | null>(null);

  const info = workspace.metrics.find((m) => m.key === metric) ?? workspace.metrics[0];
  const breakdown = info && info.groupBy.includes(groupBy) ? groupBy : "";
  const filters: MetricFilters = useMemo(
    () => ({ unitId: unitId || undefined, membershipType: membershipType || undefined, from: periodStart(weeks) }),
    [unitId, membershipType, weeks],
  );
  const key = JSON.stringify({ metric: info?.key, groupBy: breakdown, filters });

  // The report actions on the page export and schedule what is on screen.
  useEffect(() => {
    if (info) announceReportDefinition({ dashboard: "overview", metric: info.key, groupBy: breakdown || null, filters });
  }, [info, breakdown, filters]);

  useEffect(() => {
    if (!info) return;
    const controller = new AbortController();
    fetch(analyticsQueryApi, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ metric: info.key, groupBy: breakdown || null, filters }),
      signal: controller.signal,
    })
      .then(async (response) =>
        response.ok ? { key, result: (await response.json()) as MetricResult } : { key, messages: await problemMessages(response) },
      )
      .then(setAnswer, (error: unknown) => {
        if (!controller.signal.aborted) setAnswer({ key, messages: [t("failed")] });
        return error;
      });
    return () => controller.abort();
  }, [info, breakdown, filters, key, t]);

  const unitName = (id: string) => pick(workspace.units.find((u) => u.id === id)?.names ?? {}, locale);
  const groupLabel = (group: string, names: Record<string, string>) => {
    if (group === otherGroup) return t("other", { size: workspace.minGroupSize });
    if (group === noUnitGroup) return t("none");
    if (breakdown === "unit") return pick(names, locale) || group;
    if (breakdown === "channel") return t(`channel.${group}`);
    if (breakdown === "membership_type") return t(`membershipType.${group}`);
    return group;
  };
  const week = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });

  const applied: AppliedFilter[] = [
    ...(unitId ? [{ key: "unit", label: t("filter.unitValue", { name: unitName(unitId) }), onRemove: () => setUnitId("") }] : []),
    ...(membershipType
      ? [{ key: "type", label: t("filter.membershipTypeValue", { name: t(`membershipType.${membershipType}`) }), onRemove: () => setMembershipType("") }]
      : []),
  ];

  const current = answer?.key === key ? answer : null;
  const result = current?.result;
  const measure = info ? t(`measure.${info.measure}`) : "";
  const shownWeeks = result ? result.weeks.filter((w): w is { week: string; value: number } => w.value !== null) : [];
  const weekSeries = [{ key: "value", label: measure, values: shownWeeks.map((w) => w.value) }];
  const groupSeries = result ? [{ key: "value", label: measure, values: result.groups.map((g) => g.value) }] : [];
  const groupLabels = result ? result.groups.map((g) => groupLabel(g.group, g.names)) : [];

  return (
    <div className="analytics-item" data-testid="analytics-workspace">
      <FilterToolbar
        label={t("filtersLabel")}
        applied={applied}
        onClear={() => {
          setUnitId("");
          setMembershipType("");
        }}
        onSubmit={(event) => event.preventDefault()}
        end={
          <>
            <ToolbarSelect label={t("filter.metric")} value={info?.key ?? ""} onChange={(e) => setMetric(e.target.value)} data-testid="analytics-metric">
              {workspace.metrics.map((m) => (
                <option key={m.key} value={m.key}>
                  {t(`metric.${m.key}`)}
                </option>
              ))}
            </ToolbarSelect>
            <ToolbarSelect label={t("filter.groupBy")} value={breakdown} onChange={(e) => setGroupBy(e.target.value)} data-testid="analytics-group-by">
              <option value="">{t("groupBy.none")}</option>
              {info?.groupBy.map((g) => (
                <option key={g} value={g}>
                  {t(`groupBy.${g}`)}
                </option>
              ))}
            </ToolbarSelect>
          </>
        }
      >
        <ToolbarSelect label={t("filter.unit")} value={unitId} onChange={(e) => setUnitId(e.target.value)} data-testid="analytics-unit">
          <option value="">{workspace.wholeTenant ? t("filter.allUnits") : t("filter.myUnits")}</option>
          {workspace.units.map((u) => (
            <option key={u.id} value={u.id}>
              {pick(u.names, locale)}
            </option>
          ))}
        </ToolbarSelect>
        <ToolbarSelect label={t("filter.membershipType")} value={membershipType} onChange={(e) => setMembershipType(e.target.value)}>
          <option value="">{t("filter.anyone")}</option>
          {workspace.membershipTypes.map((m) => (
            <option key={m} value={m}>
              {t(`membershipType.${m}`)}
            </option>
          ))}
        </ToolbarSelect>
        <ToolbarSelect label={t("filter.period")} value={String(weeks)} onChange={(e) => setWeeks(Number(e.target.value))} data-testid="analytics-period">
          {PERIODS.map((p) => (
            <option key={p} value={p}>
              {t("period", { weeks: p })}
            </option>
          ))}
        </ToolbarSelect>
      </FilterToolbar>

      <p className="analytics-meta">{t("minimumNote", { size: workspace.minGroupSize })}</p>

      {!current ? (
        <Skeleton lines={["2rem", "15rem"]} />
      ) : current.messages ? (
        <StateView kind="error" title={t("failed")} message={current.messages.join(" ")} />
      ) : !result || result.suppressed ? (
        <div data-testid="analytics-suppressed">
          <StateView kind="empty" title={t("suppressed")} message={t("suppressedHint", { size: workspace.minGroupSize })} />
        </div>
      ) : (
        <div className="analytics-charts">
          <div data-testid="analytics-weeks-chart">
            <ChartCard
              title={t("weeks", { metric: t(`metric.${result.metric}`) })}
              description={shownWeeks.length < result.weeks.length ? t("weeksHidden", { size: result.minGroupSize }) : undefined}
              figure={result.total !== null ? <strong className="analytics-figure">{number(result.total)}</strong> : undefined}
              table={seriesTable(t("column.week"), shownWeeks.map((w) => week(w.week)), weekSeries, number)}
            >
              <LineChart
                label={t("weeksSummary", { weeks: shownWeeks.length, total: number(result.total ?? 0) })}
                categories={shownWeeks.map((w) => week(w.week))}
                series={weekSeries}
                format={number}
                area
              />
            </ChartCard>
          </div>
          {breakdown && result.groups.length > 0 ? (
            <div data-testid="analytics-groups-chart">
              <ChartCard
                title={t("groupsTitle", { groupBy: t(`groupBy.${breakdown}`) })}
                description={t("otherNote", { size: result.minGroupSize })}
                table={seriesTable(t(`groupBy.${breakdown}`), groupLabels, groupSeries, number)}
              >
                <BarChart label={t("groupsSummary", { count: result.groups.length })} categories={groupLabels} series={groupSeries} format={number} />
              </ChartCard>
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
