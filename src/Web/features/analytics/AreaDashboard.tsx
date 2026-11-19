"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { otherGroup, type AreaColumn, type AreaRow, type AreaSection, type AreaView } from "@/lib/analytics/types";
import { ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";
import { StateView } from "@/components/ui/StateView";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/**
 * One area dashboard of spec 021 section 2 (knowledge, engagement, HR or security trends): the headline figures, then
 * each section as a table, with weeks also drawn as a line. Figures behind fewer people than the minimum group size
 * arrive as null and show as hidden. Every label is a resource key of the area; the API decides what is shown.
 */
export function AreaDashboard({ view }: { view: AreaView }) {
  const t = useTranslations("analytics.areas");
  const locale = useLocale();
  const format = useFormatter();
  const a = view.area;
  const hidden = t("hidden", { size: view.minGroupSize });
  const percent = (n: number) => format.number(n, { style: "percent", maximumFractionDigits: 1 });
  const whole = (n: number) => format.number(n, { maximumFractionDigits: 1 });
  const week = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const keyLabel = (section: string, key: string) => (t.has(`${a}.${section}.keys.${key}`) ? t(`${a}.${section}.keys.${key}`) : key);

  const name = (s: AreaSection, r: AreaRow) => {
    if (r.key === otherGroup && !r.names && !r.label) {
      return t("other", { size: view.minGroupSize });
    }
    if (s.group === "week") {
      return week(r.key);
    }
    if (r.names && Object.keys(r.names).length > 0) {
      return <bdi>{pick(r.names, locale)}</bdi>;
    }
    if (s.group === "label" && r.label) {
      return <bdi>{r.label}</bdi>;
    }
    return keyLabel(s.key, r.key);
  };

  const cell = (r: AreaRow, c: AreaColumn) => {
    if (c === "date") {
      return r.date ? format.dateTime(new Date(`${r.date}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) : t("noValue");
    }
    const value = r[c];
    if (value !== null) {
      return c === "rate" ? percent(value) : whole(value);
    }
    if (r.key === otherGroup && r.count !== null) {
      return t("notCounted");
    }
    // A rate is empty, not hidden, when its group is shown but had nothing to rate.
    return c === "rate" && r.count !== null && (r.people ?? 0) >= view.minGroupSize ? t("noValue") : hidden;
  };

  const shown = view.totals.some((f) => f.value !== null && f.value > 0) || view.sections.some((s) => s.rows.length > 0);
  if (!shown) {
    return <StateView kind="empty" title={t(`${a}.empty`)} message={t(`${a}.emptyHint`)} />;
  }

  return (
    <div className="analytics-item" data-testid={`area-${a}`}>
      <dl className="analytics-stats" data-testid="area-totals">
        {view.totals.map((f) => (
          <div key={f.key}>
            <dt>{t(`${a}.total.${f.key}`)}</dt>
            <dd>{f.value === null ? (f.share ? t("noValue") : hidden) : f.share ? percent(f.value) : whole(f.value)}</dd>
          </div>
        ))}
      </dl>

      {view.sections.map((s) => {
        const id = `area-${a}-${s.key}`;
        const weeks = s.group === "week" ? s.rows.filter((r) => r.count !== null) : [];
        const series = [{ key: "count", label: t(`${a}.${s.key}.count`), values: weeks.map((r) => r.count ?? 0) }];
        return (
          <section key={s.key} className="analytics-card" aria-labelledby={id} data-testid={id}>
            <h2 id={id}>{t(`${a}.${s.key}.title`)}</h2>
            {s.rows.length === 0 ? (
              <p className="analytics-note">{t(`${a}.${s.key}.empty`)}</p>
            ) : (
              <>
                {weeks.length > 1 ? (
                  <ChartCard title={t(`${a}.${s.key}.trend`)} table={seriesTable(t("week"), weeks.map((r) => week(r.key)), series, whole)}>
                    <LineChart label={t("trendSummary", { weeks: weeks.length })} categories={weeks.map((r) => week(r.key))} series={series} format={whole} />
                  </ChartCard>
                ) : null}
                <div className="analytics-table">
                  <table>
                    <thead>
                      <tr>
                        <th scope="col">{s.group === "week" ? t("week") : t(`${a}.${s.key}.group`)}</th>
                        {s.columns.map((c) => (
                          <th scope="col" key={c}>
                            {t(`${a}.${s.key}.${c}`)}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {s.rows.map((r) => (
                        <tr key={r.key}>
                          <th scope="row">{s.link && r.key !== otherGroup ? <Link href={s.link.replace("{key}", encodeURIComponent(r.key))}>{name(s, r)}</Link> : name(s, r)}</th>
                          {s.columns.map((c) => (
                            <td key={c} data-number={c === "date" ? undefined : true}>
                              {cell(r, c)}
                            </td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </section>
        );
      })}
      <p className="analytics-note">{t(`${a}.note`, { size: view.minGroupSize })}</p>
    </div>
  );
}
