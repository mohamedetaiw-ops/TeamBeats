"use client";

import { useEffect, useId, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { direction, type Locale } from "@/i18n/locales";
import { Button } from "./Button";
import { addDays, fromIso, weekdayOf } from "./dates";

/*
 * Charts (spec 036 DLS-040). Every chart is TeamBeats SVG on the chart tokens: series take `color.chart.1` to `.8` in
 * order through `data-series`, and also differ by marker shape, line dash or bar pattern, so color is never the only
 * cue. Each chart is an image named by a one-sentence summary, and the chart card holds the same data as a table.
 * Category and time axes follow the reading direction. Charts hold no rule: the feature passes values and formats.
 */

export type Series = { key: string; label: string; values: readonly number[] };
export type Format = (value: number) => string;

const shapes = ["circle", "square", "triangle", "diamond", "circle", "square", "triangle", "diamond"] as const;
const dashes = ["", "6 4", "2 4", "10 4 2 4", "", "6 4", "2 4", "10 4 2 4"];

/** Width of the drawing in pixels, so text stays its real size at any width. */
function useWidth(initial = 560) {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(initial);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry) setWidth(Math.max(200, Math.round(entry.contentRect.width)));
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width] as const;
}

function useRtl() {
  return direction(useLocale() as Locale) === "rtl";
}

/**
 * Round axis steps: 1, 2 or 5 times a power of ten, from zero, at most five ticks. A chart of counts (every value a
 * whole number) gets whole-number steps only, so a head count never shows 0.5 (DLS-098).
 */
export function niceTicks(max: number, count = 4, integer = false): number[] {
  if (max <= 0) return [0, 1];
  const raw = max / count;
  const power = 10 ** Math.floor(Math.log10(raw));
  const nice = [1, 2, 5, 10].map((m) => m * power).find((s) => s >= raw) ?? raw;
  const step = integer ? Math.max(1, Math.ceil(nice)) : nice;
  const ticks: number[] = [];
  for (let v = 0; v <= max + step / 2; v += step) ticks.push(Number(v.toPrecision(12)));
  if ((ticks.at(-1) ?? 0) < max) ticks.push(Number(((ticks.at(-1) ?? 0) + step).toPrecision(12)));
  return ticks;
}

/** True when every value is a whole number: a chart of counts. */
function wholeNumbers(series: readonly { values: readonly number[] }[]) {
  return series.every((s) => s.values.every((v) => Number.isInteger(v)));
}

/** A series marker, drawn in the series color: circle, square, triangle or diamond by position. */
function Marker({ index, x, y, size = 4 }: { index: number; x: number; y: number; size?: number }) {
  const shape = shapes[index % shapes.length];
  if (shape === "square") return <rect className="ui-chart-marker" x={x - size} y={y - size} width={size * 2} height={size * 2} />;
  if (shape === "triangle")
    return <path className="ui-chart-marker" d={`M${x} ${y - size * 1.2}L${x + size * 1.1} ${y + size * 0.8}L${x - size * 1.1} ${y + size * 0.8}Z`} />;
  if (shape === "diamond") return <path className="ui-chart-marker" d={`M${x} ${y - size * 1.3}L${x + size * 1.3} ${y}L${x} ${y + size * 1.3}L${x - size * 1.3} ${y}Z`} />;
  return <circle className="ui-chart-marker" cx={x} cy={y} r={size} />;
}

/** Bar fills: solid for the first series, then stripes, dots and cross-hatching drawn in the surface color. */
function Patterns({ id }: { id: string }) {
  return (
    <defs>
      <pattern id={`${id}-p1`} width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
        <line x1="0" y1="0" x2="0" y2="6" className="ui-chart-hatch" />
      </pattern>
      <pattern id={`${id}-p2`} width="6" height="6" patternUnits="userSpaceOnUse">
        <circle cx="3" cy="3" r="1.1" className="ui-chart-dot" />
      </pattern>
      <pattern id={`${id}-p3`} width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
        <path d="M0 0V6M0 0H6" className="ui-chart-hatch" />
      </pattern>
    </defs>
  );
}

function patternFor(id: string, index: number) {
  const n = index % 4;
  return n === 0 ? undefined : `url(#${id}-p${n})`;
}

/** The legend: each series with its marker and pattern, so it can be matched without color. */
export function ChartLegend({ items }: { items: readonly { key: string; label: string }[] }) {
  return (
    <ul className="ui-chart-legend">
      {items.map((item, i) => (
        <li key={item.key} data-series={(i % 8) + 1}>
          <span className="ui-chart-swatch" data-shape={shapes[i % shapes.length]} data-pattern={i % 4 || undefined} aria-hidden="true" />
          <span>{item.label}</span>
        </li>
      ))}
    </ul>
  );
}

export type ChartTable = { head: readonly string[]; rows: readonly (readonly string[])[] };

/** Builds the table alternative of category charts: one row per category, one column per series. */
export function seriesTable(categoryHead: string, categories: readonly string[], series: readonly Series[], format: Format): ChartTable {
  return {
    head: [categoryHead, ...series.map((s) => s.label)],
    rows: categories.map((c, i) => [c, ...series.map((s) => format(s.values[i] ?? 0))]),
  };
}

/**
 * Chart card (spec 036 DLS-040): the title, an optional figure such as a total, the chart, its legend and a toggle
 * that shows the same data as a table. The chart itself is hidden from the toggle's table view, not removed.
 */
export function ChartCard({
  title,
  description,
  figure,
  legend,
  table,
  actions,
  children,
}: {
  title: string;
  description?: string;
  figure?: ReactNode;
  legend?: readonly { key: string; label: string }[];
  table: ChartTable;
  actions?: ReactNode;
  children: ReactNode;
}) {
  const t = useTranslations("ui.charts");
  const id = useId();
  const [asTable, setAsTable] = useState(false);
  return (
    <section className="ui-chart-card" aria-labelledby={`${id}-title`}>
      <header className="ui-chart-head">
        <div className="ui-chart-heading">
          <h3 id={`${id}-title`}>{title}</h3>
          {description ? <p>{description}</p> : null}
        </div>
        <div className="ui-chart-actions">
          {actions}
          <Button size="sm" variant="quiet" icon="menu" aria-pressed={asTable} onClick={() => setAsTable((v) => !v)}>
            {t("showTable")}
          </Button>
        </div>
      </header>
      {figure ? <div className="ui-chart-figure">{figure}</div> : null}
      {asTable ? (
        <div className="ui-table-scroll">
          <table className="ui-chart-table">
            <caption className="visually-hidden">{title}</caption>
            <thead>
              <tr>
                {table.head.map((h) => (
                  <th key={h} scope="col">
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {table.rows.map((row, r) => (
                <tr key={r}>
                  {row.map((cell, c) =>
                    c === 0 ? (
                      <th key={c} scope="row">
                        {cell}
                      </th>
                    ) : (
                      <td key={c}>{cell}</td>
                    ),
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <>
          {children}
          {legend && legend.length > 1 ? <ChartLegend items={legend} /> : null}
        </>
      )}
    </section>
  );
}

type Frame = { width: number; height: number; left: number; right: number; top: number; bottom: number };

function frame(width: number, height: number): Frame {
  return { width, height, left: 48, right: 12, top: 12, bottom: 28 };
}

/**
 * Value gridlines and labels on the start side, category labels along the bottom. SVG text takes the page direction,
 * so an "end" anchor puts a value label outside the plot on either side.
 */
function Axes({ f, ticks, max, format, categories, x, rtl }: { f: Frame; ticks: number[]; max: number; format: Format; categories: readonly string[]; x: (i: number) => number; rtl: boolean }) {
  const y = (v: number) => f.top + (f.height - f.top - f.bottom) * (1 - v / max);
  const every = Math.max(1, Math.ceil(categories.length / Math.max(1, Math.floor((f.width - f.left - f.right) / 64))));
  return (
    <g className="ui-chart-axes" aria-hidden="true">
      {ticks.map((v) => (
        <g key={v}>
          <line className="ui-chart-grid" x1={f.left} x2={f.width - f.right} y1={y(v)} y2={y(v)} />
          <text x={rtl ? f.width - f.right + 6 : f.left - 6} y={y(v)} dy="0.32em" textAnchor="end">
            {format(v)}
          </text>
        </g>
      ))}
      {categories.map((c, i) =>
        i % every === 0 ? (
          <text key={c} x={x(i)} y={f.height - 8} textAnchor="middle">
            {c}
          </text>
        ) : null,
      )}
    </g>
  );
}

/** In right to left, the value axis sits on the right, so the plot keeps its margins mirrored. */
function mirrored(f: Frame, rtl: boolean): Frame {
  return rtl ? { ...f, left: f.right, right: f.left } : f;
}

/**
 * Line chart, area chart and step chart (spec 036 DLS-040): one line per series with its own marker and dash. `area`
 * fills under each line; `stacked` stacks the areas; `curve="step"` draws steps for values that change in jumps.
 */
export function LineChart({
  label,
  categories,
  series,
  format,
  area = false,
  stacked = false,
  curve = "linear",
  height = 240,
}: {
  label: string;
  categories: readonly string[];
  series: readonly Series[];
  format: Format;
  area?: boolean;
  stacked?: boolean;
  curve?: "linear" | "step";
  height?: number;
}) {
  const t = useTranslations("ui.charts");
  const [ref, width] = useWidth();
  const rtl = useRtl();
  const f = mirrored(frame(width, height), rtl);
  const totals = categories.map((_, i) => series.reduce((sum, s) => sum + (s.values[i] ?? 0), 0));
  const top = stacked ? Math.max(...totals) : Math.max(...series.flatMap((s) => s.values));
  const ticks = niceTicks(top, 4, wholeNumbers(series));
  const max = ticks.at(-1) ?? 1;
  const span = f.width - f.left - f.right;
  const x = (i: number) => {
    const pos = f.left + (categories.length > 1 ? (span * i) / (categories.length - 1) : span / 2);
    return rtl ? f.left + f.width - f.right - pos : pos;
  };
  const y = (v: number) => f.top + (f.height - f.top - f.bottom) * (1 - v / max);
  const base = categories.map(() => 0);
  const layers = series.map((s) => {
    const low = [...base];
    const high = s.values.map((v, i) => (stacked ? (base[i] ?? 0) + v : v));
    if (stacked) high.forEach((v, i) => (base[i] = v));
    return { s, low, high };
  });
  const path = (values: readonly number[]) =>
    values
      .map((v, i) => {
        const point = `${x(i)} ${y(v)}`;
        if (i === 0) return `M${point}`;
        return curve === "step" ? `H${x(i)}V${y(v)}` : `L${point}`;
      })
      .join("");
  if (categories.length === 1) {
    // One point is a value, not a trend: show it as a stat with its date instead of a lone dot (DLS-098).
    return (
      <div ref={ref} className="ui-chart ui-chart-single" role="img" aria-label={label}>
        {series.map((s) => (
          <p key={s.key}>
            <strong>{format(s.values[0] ?? 0)}</strong>
            <span>{t("single", { series: s.label, category: categories[0] ?? "" })}</span>
          </p>
        ))}
      </div>
    );
  }
  return (
    <div ref={ref} className="ui-chart">
      <svg width={width} height={height} role="img" aria-label={label}>
        <Axes f={f} ticks={ticks} max={max} format={format} categories={categories} x={x} rtl={rtl} />
        {layers.map(({ s, low, high }, n) => (
          <g key={s.key} data-series={(n % 8) + 1}>
            {area ? (
              <path
                className="ui-chart-area"
                d={`${path(high)}L${x(categories.length - 1)} ${y(low.at(-1) ?? 0)}${[...low]
                  .map((v, i) => `L${x(i)} ${y(v)}`)
                  .reverse()
                  .join("")}Z`}
              />
            ) : null}
            <path className="ui-chart-line" d={path(high)} strokeDasharray={dashes[n % dashes.length] || undefined} />
            {high.map((v, i) => (
              <g key={i}>
                <Marker index={n} x={x(i)} y={y(v)} />
                <title>{t("point", { series: s.label, category: categories[i] ?? "", value: format(s.values[i] ?? 0) })}</title>
              </g>
            ))}
          </g>
        ))}
      </svg>
    </div>
  );
}

/**
 * Bar chart and combined chart (spec 036 DLS-040): grouped or stacked bars per category, each series with its own
 * pattern; `line` adds one series as a line on the same scale, such as a target or an average.
 */
export function BarChart({
  label,
  categories,
  series,
  format,
  stacked = false,
  line,
  height = 240,
}: {
  label: string;
  categories: readonly string[];
  series: readonly Series[];
  format: Format;
  stacked?: boolean;
  line?: Series;
  height?: number;
}) {
  const t = useTranslations("ui.charts");
  const id = useId().replace(/:/g, "");
  const [ref, width] = useWidth();
  const rtl = useRtl();
  const f = mirrored(frame(width, height), rtl);
  const totals = categories.map((_, i) => series.reduce((sum, s) => sum + (s.values[i] ?? 0), 0));
  const top = Math.max(stacked ? Math.max(...totals) : Math.max(...series.flatMap((s) => s.values)), ...(line?.values ?? [0]));
  const ticks = niceTicks(top, 4, wholeNumbers(line ? [...series, line] : series));
  const max = ticks.at(-1) ?? 1;
  const span = f.width - f.left - f.right;
  const band = span / categories.length;
  const center = (i: number) => {
    const pos = f.left + band * (i + 0.5);
    return rtl ? f.left + f.width - f.right - pos : pos;
  };
  const y = (v: number) => f.top + (f.height - f.top - f.bottom) * (1 - v / max);
  const groupWidth = band * 0.7;
  const barWidth = stacked ? groupWidth : groupWidth / series.length;
  const lineIndex = series.length;
  return (
    <div ref={ref} className="ui-chart">
      <svg width={width} height={height} role="img" aria-label={label}>
        <Patterns id={id} />
        <Axes f={f} ticks={ticks} max={max} format={format} categories={categories} x={center} rtl={rtl} />
        {categories.map((c, i) => {
          let stack = 0;
          return (
            <g key={c}>
              {series.map((s, n) => {
                const v = s.values[i] ?? 0;
                const from = stacked ? stack : 0;
                stack += v;
                const offset = stacked ? -groupWidth / 2 : -groupWidth / 2 + n * barWidth;
                const xPos = rtl ? center(i) - offset - barWidth : center(i) + offset;
                const fill = patternFor(id, n);
                return (
                  <g key={s.key} data-series={(n % 8) + 1} className="ui-chart-bar">
                    <rect x={xPos + 1} y={y(from + v)} width={Math.max(1, barWidth - 2)} height={Math.max(0, y(from) - y(from + v))} rx={stacked ? 0 : 3} />
                    {fill ? <rect className="ui-chart-pattern" x={xPos + 1} y={y(from + v)} width={Math.max(1, barWidth - 2)} height={Math.max(0, y(from) - y(from + v))} fill={fill} /> : null}
                    <title>{t("point", { series: s.label, category: c, value: format(v) })}</title>
                  </g>
                );
              })}
            </g>
          );
        })}
        {line ? (
          <g data-series={(lineIndex % 8) + 1}>
            <path className="ui-chart-line" strokeDasharray="6 4" d={line.values.map((v, i) => `${i ? "L" : "M"}${center(i)} ${y(v)}`).join("")} />
            {line.values.map((v, i) => (
              <g key={i}>
                <Marker index={lineIndex} x={center(i)} y={y(v)} />
                <title>{t("point", { series: line.label, category: categories[i] ?? "", value: format(v) })}</title>
              </g>
            ))}
          </g>
        ) : null}
      </svg>
    </div>
  );
}

/** Bar list (spec 036 DLS-040): ranked rows with a bar behind each label and the value at the end. Plain HTML. */
export function BarList({ label, items }: { label: string; items: readonly { key: string; label: string; value: number; valueText: string }[] }) {
  const max = Math.max(1, ...items.map((i) => i.value));
  return (
    <ol className="ui-barlist" aria-label={label}>
      {items.map((item) => (
        <li key={item.key} style={{ "--xp-share": `${(item.value / max) * 100}%` } as CSSProperties}>
          <span className="ui-barlist-label">
            <bdi>{item.label}</bdi>
          </span>
          <span className="ui-barlist-value">{item.valueText}</span>
        </li>
      ))}
    </ol>
  );
}

/** Funnel (spec 036 DLS-040): stages that narrow, each with its value and its share of the first stage. */
export function FunnelChart({ label, stages, format }: { label: string; stages: readonly { key: string; label: string; value: number }[]; format: Format }) {
  const t = useTranslations("ui.charts");
  const locale = useLocale();
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 0 });
  const first = stages[0]?.value || 1;
  return (
    <ol className="ui-funnel" aria-label={label}>
      {stages.map((s, i) => (
        <li key={s.key} data-series={1} style={{ "--xp-share": `${Math.max(4, (s.value / first) * 100)}%` } as CSSProperties}>
          <span className="ui-funnel-bar" aria-hidden="true" />
          <span className="ui-funnel-text">
            <span className="ui-funnel-label">{s.label}</span>
            <span className="ui-funnel-value">
              {format(s.value)}
              {i > 0 ? <span>{t("ofFirst", { share: percent.format(s.value / first) })}</span> : null}
            </span>
          </span>
        </li>
      ))}
    </ol>
  );
}

/** Heat map (spec 036 DLS-040): a grid of cells whose strength shows the value; each cell also carries it as text. */
export function HeatMap({ label, rows, columns, values, format }: { label: string; rows: readonly string[]; columns: readonly string[]; values: readonly (readonly number[])[]; format: Format }) {
  const max = Math.max(1, ...values.flat());
  return (
    <div className="ui-heatmap" role="img" aria-label={label} style={{ "--xp-columns": columns.length } as CSSProperties}>
      <span aria-hidden="true" />
      {columns.map((c) => (
        <span key={c} className="ui-heatmap-head" aria-hidden="true">
          {c}
        </span>
      ))}
      {rows.map((r, ri) => (
        <div key={r} className="ui-heatmap-row">
          <span className="ui-heatmap-head" aria-hidden="true">
            {r}
          </span>
          {columns.map((c, ci) => {
            const v = values[ri]?.[ci] ?? 0;
            return (
              <span key={c} className="ui-heatmap-cell" data-level={Math.min(4, Math.ceil((v / max) * 4))} title={`${r}, ${c}: ${format(v)}`} aria-hidden="true">
                {format(v)}
              </span>
            );
          })}
        </div>
      ))}
    </div>
  );
}

/** Radar (spec 036 DLS-040): a few series compared on the same axes, such as skills or survey themes. */
export function RadarChart({ label, axes, series, max, size = 280 }: { label: string; axes: readonly string[]; series: readonly Series[]; max: number; size?: number }) {
  const t = useTranslations("ui.charts");
  const rtl = useRtl();
  const c = size / 2;
  const r = c - 44;
  const angle = (i: number) => (rtl ? -1 : 1) * ((Math.PI * 2 * i) / axes.length) - Math.PI / 2;
  const point = (i: number, v: number) => [c + Math.cos(angle(i)) * r * (v / max), c + Math.sin(angle(i)) * r * (v / max)] as const;
  return (
    <div className="ui-chart ui-chart-center">
      <svg width={size} height={size} role="img" aria-label={label}>
        <g className="ui-chart-axes" aria-hidden="true">
          {[0.25, 0.5, 0.75, 1].map((ring) => (
            <polygon key={ring} className="ui-chart-grid" points={axes.map((_, i) => point(i, max * ring).join(",")).join(" ")} />
          ))}
          {axes.map((a, i) => {
            const [x, y] = point(i, max * 1.18);
            return (
              <g key={a}>
                <line className="ui-chart-grid" x1={c} y1={c} x2={point(i, max)[0]} y2={point(i, max)[1]} />
                <text x={x} y={y} dy="0.32em" textAnchor="middle">
                  {a}
                </text>
              </g>
            );
          })}
        </g>
        {series.map((s, n) => (
          <g key={s.key} data-series={(n % 8) + 1}>
            <polygon className="ui-chart-area ui-chart-line" strokeDasharray={dashes[n % dashes.length] || undefined} points={s.values.map((v, i) => point(i, v).join(",")).join(" ")} />
            {s.values.map((v, i) => (
              <g key={i}>
                <Marker index={n} x={point(i, v)[0]} y={point(i, v)[1]} size={3.5} />
                <title>{t("point", { series: s.label, category: axes[i] ?? "", value: String(v) })}</title>
              </g>
            ))}
          </g>
        ))}
      </svg>
    </div>
  );
}

function arc(c: number, r: number, from: number, to: number) {
  const a = (t: number) => [c + r * Math.cos(t), c + r * Math.sin(t)] as const;
  const [x1, y1] = a(from);
  const [x2, y2] = a(to);
  return `M${x1} ${y1}A${r} ${r} 0 ${to - from > Math.PI ? 1 : 0} 1 ${x2} ${y2}`;
}

/** Radial chart (spec 036 DLS-040): one value against its goal as an arc, with the value written in the middle. */
export function RadialChart({ label, value, max, valueText, caption, size = 180 }: { label: string; value: number; max: number; valueText: string; caption?: string; size?: number }) {
  const c = size / 2;
  const r = c - 12;
  const start = Math.PI * 0.75;
  const sweep = Math.PI * 1.5;
  const share = Math.max(0, Math.min(1, value / max));
  return (
    <div className="ui-chart ui-chart-center ui-radial">
      <svg width={size} height={size} role="img" aria-label={label}>
        <path className="ui-chart-track" d={arc(c, r, start, start + sweep)} />
        {share > 0 ? (
          <g data-series={1}>
            <path className="ui-chart-arc" d={arc(c, r, start, start + sweep * share)} />
          </g>
        ) : null}
      </svg>
      <span className="ui-radial-value" aria-hidden="true">
        <strong>{valueText}</strong>
        {caption ? <span>{caption}</span> : null}
      </span>
    </div>
  );
}

/** Rings (spec 036 DLS-040): a few goals as nested rings, each named with its value in the list beside it. */
export function Rings({ label, rings, size = 180 }: { label: string; rings: readonly { key: string; label: string; value: number; max: number; valueText: string }[]; size?: number }) {
  const c = size / 2;
  return (
    <div className="ui-rings">
      <div className="ui-chart ui-chart-center">
        <svg width={size} height={size} role="img" aria-label={label}>
          {rings.map((ring, n) => {
            const r = c - 10 - n * 18;
            const share = Math.max(0, Math.min(0.999, ring.value / ring.max));
            return (
              <g key={ring.key} data-series={(n % 8) + 1}>
                <circle className="ui-chart-track" cx={c} cy={c} r={r} />
                {share > 0 ? <path className="ui-chart-arc" d={arc(c, r, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * share)} /> : null}
              </g>
            );
          })}
        </svg>
      </div>
      <ul className="ui-chart-legend ui-rings-list">
        {rings.map((ring, n) => (
          <li key={ring.key} data-series={(n % 8) + 1}>
            <span className="ui-chart-swatch" data-shape={shapes[n % shapes.length]} aria-hidden="true" />
            <span>{ring.label}</span>
            <strong>{ring.valueText}</strong>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Flow (spec 036 DLS-040): how amounts move from sources to destinations, as bands whose width is the amount. */
export function FlowChart({
  label,
  sources,
  targets,
  links,
  format,
  height = 240,
}: {
  label: string;
  sources: readonly { key: string; label: string }[];
  targets: readonly { key: string; label: string }[];
  links: readonly { from: string; to: string; value: number }[];
  format: Format;
  height?: number;
}) {
  const t = useTranslations("ui.charts");
  const [ref, width] = useWidth();
  const rtl = useRtl();
  const total = links.reduce((sum, l) => sum + l.value, 0) || 1;
  const gap = 10;
  const node = 12;
  const labelSpace = Math.min(150, width * 0.28);
  const startX = rtl ? width - labelSpace - node : labelSpace;
  const endX = rtl ? labelSpace : width - labelSpace - node;
  const place = (keys: readonly string[], side: "from" | "to") => {
    const usable = height - gap * (keys.length - 1);
    let y = 0;
    return new Map(
      keys.map((k) => {
        const value = links.filter((l) => l[side] === k).reduce((sum, l) => sum + l.value, 0);
        const h = (value / total) * usable;
        const at = { y, h, value };
        y += h + gap;
        return [k, at];
      }),
    );
  };
  const left = place(
    sources.map((s) => s.key),
    "from",
  );
  const right = place(
    targets.map((s) => s.key),
    "to",
  );
  const outUsed = new Map<string, number>();
  const inUsed = new Map<string, number>();
  return (
    <div ref={ref} className="ui-chart">
      <svg width={width} height={height} role="img" aria-label={label}>
        {links.map((l) => {
          const a = left.get(l.from);
          const b = right.get(l.to);
          if (!a || !b) return null;
          const h = (l.value / total) * (height - gap * (sources.length - 1));
          const h2 = (l.value / total) * (height - gap * (targets.length - 1));
          const y1 = a.y + (outUsed.get(l.from) ?? 0);
          const y2 = b.y + (inUsed.get(l.to) ?? 0);
          outUsed.set(l.from, (outUsed.get(l.from) ?? 0) + h);
          inUsed.set(l.to, (inUsed.get(l.to) ?? 0) + h2);
          const x1 = rtl ? startX : startX + node;
          const x2 = rtl ? endX + node : endX;
          const mid = (x1 + x2) / 2;
          const index = sources.findIndex((s) => s.key === l.from);
          return (
            <g key={`${l.from}-${l.to}`} data-series={(index % 8) + 1}>
              <path className="ui-chart-flow" d={`M${x1} ${y1}C${mid} ${y1} ${mid} ${y2} ${x2} ${y2}V${y2 + h2}C${mid} ${y2 + h2} ${mid} ${y1 + h} ${x1} ${y1 + h}Z`} />
              <title>{t("flow", { from: sources[index]?.label ?? "", to: targets.find((x) => x.key === l.to)?.label ?? "", value: format(l.value) })}</title>
            </g>
          );
        })}
        {sources.map((s, n) => {
          const at = left.get(s.key)!;
          return (
            <g key={s.key} data-series={(n % 8) + 1} aria-hidden="true">
              <rect className="ui-chart-node" x={startX} y={at.y} width={node} height={Math.max(2, at.h)} rx={2} />
              <text className="ui-chart-node-label" x={rtl ? startX + node + 8 : startX - 8} y={at.y + at.h / 2} dy="0.32em" textAnchor="end">
                {t("node", { label: s.label, value: format(at.value) })}
              </text>
            </g>
          );
        })}
        {targets.map((s) => {
          const at = right.get(s.key)!;
          return (
            <g key={s.key} aria-hidden="true">
              <rect className="ui-chart-node ui-chart-node-target" x={endX} y={at.y} width={node} height={Math.max(2, at.h)} rx={2} />
              <text className="ui-chart-node-label" x={rtl ? endX - 8 : endX + node + 8} y={at.y + at.h / 2} dy="0.32em" textAnchor="start">
                {t("node", { label: s.label, value: format(at.value) })}
              </text>
            </g>
          );
        })}
      </svg>
    </div>
  );
}

/** Scatter (spec 036 DLS-040): points on two value axes, each series with its own marker shape. */
export function ScatterChart({
  label,
  series,
  formatX,
  formatY,
  height = 240,
}: {
  label: string;
  series: readonly { key: string; label: string; points: readonly { x: number; y: number; name?: string }[] }[];
  formatX: Format;
  formatY: Format;
  height?: number;
}) {
  const t = useTranslations("ui.charts");
  const [ref, width] = useWidth();
  const rtl = useRtl();
  const f = mirrored(frame(width, height), rtl);
  const xs = niceTicks(Math.max(...series.flatMap((s) => s.points.map((p) => p.x))));
  const ys = niceTicks(Math.max(...series.flatMap((s) => s.points.map((p) => p.y))));
  const maxX = xs.at(-1) ?? 1;
  const maxY = ys.at(-1) ?? 1;
  const x = (v: number) => {
    const pos = f.left + (f.width - f.left - f.right) * (v / maxX);
    return rtl ? f.left + f.width - f.right - pos : pos;
  };
  const y = (v: number) => f.top + (f.height - f.top - f.bottom) * (1 - v / maxY);
  return (
    <div ref={ref} className="ui-chart">
      <svg width={width} height={height} role="img" aria-label={label}>
        <g className="ui-chart-axes" aria-hidden="true">
          {ys.map((v) => (
            <g key={`y${v}`}>
              <line className="ui-chart-grid" x1={f.left} x2={f.width - f.right} y1={y(v)} y2={y(v)} />
              <text x={rtl ? f.width - f.right + 6 : f.left - 6} y={y(v)} dy="0.32em" textAnchor="end">
                {formatY(v)}
              </text>
            </g>
          ))}
          {xs.map((v) => (
            <text key={`x${v}`} x={x(v)} y={f.height - 8} textAnchor="middle">
              {formatX(v)}
            </text>
          ))}
        </g>
        {series.map((s, n) => (
          <g key={s.key} data-series={(n % 8) + 1} className="ui-chart-points">
            {s.points.map((p, i) => (
              <g key={i}>
                <Marker index={n} x={x(p.x)} y={y(p.y)} size={5} />
                <title>{t("xy", { name: p.name ?? s.label, x: formatX(p.x), y: formatY(p.y) })}</title>
              </g>
            ))}
          </g>
        ))}
      </svg>
    </div>
  );
}

/**
 * Contributions (spec 036 DLS-040): activity per day over weeks, as squares in five steps of strength. The squares are
 * a picture; the summary and the table say the numbers.
 */
export function Contributions({ label, start, values, weekStart = 0 }: { label: string; start: string; values: readonly number[]; weekStart?: number }) {
  const locale = useLocale();
  const day = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" });
  const max = Math.max(1, ...values);
  const lead = (weekdayOf(start) - weekStart + 7) % 7;
  const cells = [...Array<null>(lead).fill(null), ...values.map((v, i) => ({ v, iso: addDays(start, i) }))];
  return (
    <div className="ui-contrib" role="img" aria-label={label}>
      {cells.map((cell, i) =>
        cell ? (
          <span key={i} className="ui-contrib-day" data-level={cell.v === 0 ? 0 : Math.min(4, Math.ceil((cell.v / max) * 4))} title={`${day.format(fromIso(cell.iso))}: ${cell.v}`} />
        ) : (
          <span key={i} />
        ),
      )}
    </div>
  );
}

/** Stage bars (spec 036 DLS-040): how one whole splits into stages, as one bar with each part named and counted. */
export function StageBars({ label, stages, format }: { label: string; stages: readonly { key: string; label: string; value: number }[]; format: Format }) {
  const total = stages.reduce((sum, s) => sum + s.value, 0) || 1;
  const locale = useLocale();
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 0 });
  return (
    <div className="ui-stagebars">
      <div className="ui-stagebar" role="img" aria-label={label}>
        {stages.map((s, n) => (
          <span key={s.key} data-series={(n % 8) + 1} data-pattern={n % 4 || undefined} style={{ flexGrow: s.value }} />
        ))}
      </div>
      <ul className="ui-chart-legend">
        {stages.map((s, n) => (
          <li key={s.key} data-series={(n % 8) + 1}>
            <span className="ui-chart-swatch" data-shape="square" data-pattern={n % 4 || undefined} aria-hidden="true" />
            <span>{s.label}</span>
            <strong>
              {format(s.value)} <span>{percent.format(s.value / total)}</span>
            </strong>
          </li>
        ))}
      </ul>
    </div>
  );
}
