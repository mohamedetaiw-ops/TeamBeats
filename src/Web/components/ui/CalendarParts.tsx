"use client";

import { useId, useSyncExternalStore, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Drawer } from "./Dialog";
import { CloseButton, IconButton } from "./IconButton";
import { Icon, type IconName } from "./Icon";
import { addDays, fromIso, monthGrid, monthStart, firstWeekday } from "./dates";

/**
 * The calendar parts of spec 036 DLS-069 to DLS-074, built for the unified calendar of spec 037 and usable by any
 * feature: a header, layer chips that are the legend, a week strip, agenda rows, a month with continuous ranges, a day
 * panel that becomes a bottom sheet on small screens, and a team availability heatmap. They hold no product rules:
 * the feature passes the items, labels and colour steps (1 to 8, the chart tokens).
 */

export type Swatch = number;

/** Calendar header (DLS-069): title and period at the start; previous, Today and next at the end. */
export function CalendarHeader({
  title,
  period,
  onPrevious,
  onToday,
  onNext,
  previousLabel,
  nextLabel,
  todayLabel,
  level = 2,
  children,
}: {
  title: string;
  period: string;
  onPrevious: () => void;
  onToday: () => void;
  onNext: () => void;
  previousLabel: string;
  nextLabel: string;
  todayLabel: string;
  level?: 2 | 3;
  /** The view switch, under the header. */
  children?: ReactNode;
}) {
  const Heading = level === 2 ? "h2" : "h3";
  return (
    <div className="ui-cal-header">
      <div className="ui-cal-header-row">
        <div className="ui-cal-heading">
          <Heading className="ui-cal-title">{title}</Heading>
          <p className="ui-cal-period" aria-live="polite">
            {period}
          </p>
        </div>
        <div className="ui-cal-nav">
          <IconButton icon="chevron-back" label={previousLabel} size="sm" onClick={onPrevious} />
          <button type="button" className="button button-secondary ui-cal-today" data-size="sm" onClick={onToday}>
            {todayLabel}
          </button>
          <IconButton icon="chevron-forward" label={nextLabel} size="sm" onClick={onNext} />
        </div>
      </div>
      {children}
    </div>
  );
}

/** A layer's colour dot; decorative, since the label always says the layer. */
export function LayerDot({ swatch }: { swatch: Swatch }) {
  return <span className="ui-cal-dot" data-swatch={swatch} aria-hidden="true" />;
}

export type LayerOption = { key: string; label: string; icon: IconName; swatch: Swatch };

/** Layer chips (DLS-070): toggle buttons with a pressed state; they are the legend, so each shows its colour and icon. */
export function LayerChips({
  label,
  layers,
  selected,
  onToggle,
}: {
  label: string;
  layers: readonly LayerOption[];
  selected: readonly string[];
  onToggle?: (key: string) => void;
}) {
  return (
    <div className="ui-cal-layers" role="group" aria-label={label}>
      {layers.map((layer) =>
        onToggle ? (
          <button
            key={layer.key}
            type="button"
            className="ui-cal-layer"
            data-swatch={layer.swatch}
            aria-pressed={selected.includes(layer.key)}
            onClick={() => onToggle(layer.key)}
          >
            <LayerDot swatch={layer.swatch} />
            <Icon name={layer.icon} size={14} />
            <span>{layer.label}</span>
          </button>
        ) : (
          <span key={layer.key} className="ui-cal-layer" data-swatch={layer.swatch} data-static="true">
            <LayerDot swatch={layer.swatch} />
            <span>{layer.label}</span>
          </span>
        ),
      )}
    </div>
  );
}

export type StripDay = { date: string; markers: readonly Swatch[]; count: number };

/** Week strip (DLS-071): seven days, weekday initial and number, up to four markers and a hidden count. */
export function WeekStrip({
  label,
  days,
  selected,
  today,
  onSelect,
  countLabel,
}: {
  label: string;
  days: readonly StripDay[];
  selected: string;
  today: string;
  onSelect: (date: string) => void;
  countLabel: (count: number) => string;
}) {
  const locale = useLocale();
  const t = useTranslations("ui");
  const initial = new Intl.DateTimeFormat(locale, { weekday: "narrow", timeZone: "UTC" });
  const number = new Intl.DateTimeFormat(locale, { day: "numeric", timeZone: "UTC" });
  const full = new Intl.DateTimeFormat(locale, { dateStyle: "full", timeZone: "UTC" });
  return (
    <ol className="ui-cal-strip" aria-label={label}>
      {days.map((d) => (
        <li key={d.date}>
          <button
            type="button"
            className="ui-cal-strip-day"
            aria-pressed={d.date === selected}
            aria-current={d.date === today ? "date" : undefined}
            onClick={() => onSelect(d.date)}
          >
            <span className="ui-cal-strip-initial" aria-hidden="true">
              {initial.format(fromIso(d.date))}
            </span>
            <span className="ui-cal-strip-number" aria-hidden="true">
              {number.format(fromIso(d.date))}
            </span>
            <span className="ui-cal-markers" aria-hidden="true">
              {d.markers.slice(0, 4).map((m, i) => (
                <LayerDot key={i} swatch={m} />
              ))}
            </span>
            <span className="visually-hidden">
              {t("data.dayCount", { date: full.format(fromIso(d.date)), count: countLabel(d.count) })}
            </span>
          </button>
        </li>
      ))}
    </ol>
  );
}

/**
 * Agenda row (DLS-071): a 3px layer bar, the time or "All day", the title, then the place or source. Pending items are
 * dashed, cancelled ones struck through with the word. The row opens in place to its details and actions.
 */
export function AgendaRow({
  swatch,
  icon,
  layerLabel,
  time,
  title,
  meta,
  status,
  statusLabel,
  primary,
  details,
  open,
  onToggle,
}: {
  swatch: Swatch;
  icon: IconName;
  layerLabel: string;
  time: string;
  title: string;
  meta?: string | null;
  status?: "pending" | "cancelled" | "tentative" | null;
  statusLabel?: string | null;
  /** At most one primary action, shown on the row (Join while a meeting is live). */
  primary?: ReactNode;
  details?: ReactNode;
  open?: boolean;
  onToggle?: () => void;
}) {
  const id = useId();
  const body = (
    <>
      <span className="ui-cal-row-line">
        <span className="ui-cal-row-time">{time}</span>
        {statusLabel ? <span className="ui-cal-row-status">{statusLabel}</span> : null}
      </span>
      {/* The title's box follows the page direction, so a title in another script starts at the same edge (DLS-029). */}
      <span className="ui-cal-row-title">
        <bdi>{title}</bdi>
      </span>
      <span className="ui-cal-row-meta">
        <Icon name={icon} size={12} />
        <span className="visually-hidden">{layerLabel}</span>
        {meta ? <bdi>{meta}</bdi> : <span aria-hidden="true">{layerLabel}</span>}
      </span>
    </>
  );
  return (
    <li className="ui-cal-row" data-swatch={swatch} data-status={status ?? undefined} data-open={open || undefined}>
      <div className="ui-cal-row-main">
        {details && onToggle ? (
          <button type="button" className="ui-cal-row-toggle" aria-expanded={open ?? false} aria-controls={id} onClick={onToggle}>
            {body}
          </button>
        ) : (
          <div className="ui-cal-row-toggle">{body}</div>
        )}
        {primary ? <div className="ui-cal-row-primary">{primary}</div> : null}
      </div>
      {details ? (
        <div id={id} className="ui-cal-row-details" hidden={!open}>
          {details}
        </div>
      ) : null}
    </li>
  );
}

export type MonthBar = {
  id: string;
  /** First day, ISO. */
  start: string;
  /** Exclusive end, ISO. */
  end: string;
  label: string;
  swatch: Swatch;
  kind: "holiday" | "leave" | "event";
  pending?: boolean;
};

/** Lanes of the bars that touch one week, at most `max`; the rest are counted per day. */
function weekLanes(bars: readonly MonthBar[], week: readonly string[], max: number) {
  const first = week[0]!;
  const after = addDays(week[6]!, 1);
  const touching = bars.filter((b) => b.start < after && b.end > first).sort((a, b) => a.start.localeCompare(b.start) || b.end.localeCompare(a.end));
  const lanes: { bar: MonthBar; from: number; to: number }[][] = [];
  const hidden = new Map<string, number>();
  for (const bar of touching) {
    const from = Math.max(0, week.indexOf(bar.start < first ? first : bar.start));
    const to = bar.end >= after ? 7 : week.indexOf(bar.end);
    const lane = lanes.findIndex((l) => l.every((s) => s.to <= from || s.from >= to));
    if (lane >= 0) lanes[lane]!.push({ bar, from, to });
    else if (lanes.length < max) lanes.push([{ bar, from, to }]);
    else for (let i = from; i < to; i += 1) hidden.set(week[i]!, (hidden.get(week[i]!) ?? 0) + 1);
  }
  return { lanes: lanes.map((l) => l.sort((a, b) => a.from - b.from)), hidden };
}

/**
 * Month with ranges (DLS-072): a table with weekday headers; under each week, multi-day items as one continuous bar
 * per lane (rounded only where the range starts and ends), holidays styled apart from leave; single-day items as
 * markers with "+N". Selecting a day is the feature's (the day panel).
 */
export function RangeMonth({
  month,
  today,
  selected,
  weekStart,
  bars,
  markers,
  items,
  onSelect,
  countLabel,
  maxLanes = 3,
}: {
  month: string;
  today: string;
  selected?: string | null;
  weekStart?: number;
  bars: readonly MonthBar[];
  markers: Readonly<Record<string, readonly Swatch[]>>;
  /** The single-day items of each day, in order. Where there is room a cell names up to three and counts the rest (DLS-119). */
  items?: Readonly<Record<string, readonly { id: string; label: string; swatch: Swatch }[]>>;
  onSelect: (date: string) => void;
  countLabel: (count: number) => string;
  maxLanes?: number;
}) {
  const locale = useLocale();
  const t = useTranslations("ui");
  const start = monthStart(month, "gregory");
  const first = weekStart ?? firstWeekday(locale);
  const grid = monthGrid(start, "gregory", first);
  // Fill the days around the month so ranges run across whole weeks.
  const weeks = grid.map((week) => {
    const known = week.findIndex(Boolean);
    const anchor = week[known]!;
    return week.map((_, i) => addDays(anchor, i - known));
  });
  const title = new Intl.DateTimeFormat(locale, { month: "long", year: "numeric", timeZone: "UTC" }).format(fromIso(start));
  const longDay = new Intl.DateTimeFormat(locale, { weekday: "long", timeZone: "UTC" });
  const shortDay = new Intl.DateTimeFormat(locale, { weekday: "short", timeZone: "UTC" });
  const number = new Intl.DateTimeFormat(locale, { day: "numeric", timeZone: "UTC" });
  const full = new Intl.DateTimeFormat(locale, { dateStyle: "full", timeZone: "UTC" });
  const inMonth = (d: string) => d.slice(0, 7) === start.slice(0, 7);

  return (
    <div className="ui-cal-month" data-items={items ? true : undefined}>
      <table aria-label={title}>
        <thead>
          <tr>
            {weeks[0]!.map((d) => (
              <th key={d} scope="col">
                <abbr title={longDay.format(fromIso(d))}>{shortDay.format(fromIso(d))}</abbr>
              </th>
            ))}
          </tr>
        </thead>
        {weeks.map((week) => {
          const { lanes, hidden } = weekLanes(bars, week, maxLanes);
          return (
            <tbody key={week[0]} className="ui-cal-month-week">
              <tr>
                {week.map((d) => {
                  const dots = markers[d] ?? [];
                  const more = Math.max(0, dots.length - 3) + (hidden.get(d) ?? 0);
                  return (
                    <td key={d} data-outside={!inMonth(d) || undefined}>
                      <button
                        type="button"
                        className="ui-cal-month-day"
                        aria-current={d === today ? "date" : undefined}
                        aria-pressed={d === selected}
                        onClick={() => onSelect(d)}
                      >
                        <span aria-hidden="true">{number.format(fromIso(d))}</span>
                        {items ? (
                          <span className="ui-cal-month-items" aria-hidden="true">
                            {(items[d] ?? []).slice(0, 3).map((item) => (
                              <span key={item.id} className="ui-cal-month-item" data-swatch={item.swatch}>
                                <LayerDot swatch={item.swatch} />
                                <bdi>{item.label}</bdi>
                              </span>
                            ))}
                            {more > 0 ? <span className="ui-cal-more">{t("data.more", { count: more })}</span> : null}
                          </span>
                        ) : null}
                        <span className="ui-cal-markers" aria-hidden="true">
                          {dots.slice(0, 3).map((m, i) => (
                            <LayerDot key={i} swatch={m} />
                          ))}
                          {more > 0 ? <span className="ui-cal-more">{t("data.more", { count: more })}</span> : null}
                        </span>
                        <span className="visually-hidden">
                          {t("data.dayCount", { date: full.format(fromIso(d)), count: countLabel(dots.length + (hidden.get(d) ?? 0)) })}
                        </span>
                      </button>
                    </td>
                  );
                })}
              </tr>
              {lanes.map((lane, i) => {
                const cells: ReactNode[] = [];
                let at = 0;
                for (const s of lane) {
                  if (s.from > at) cells.push(<td key={`gap-${at}`} colSpan={s.from - at} />);
                  const opens = s.bar.start >= week[0]!;
                  const closes = s.bar.end <= addDays(week[6]!, 1);
                  cells.push(
                    <td key={s.bar.id} colSpan={s.to - s.from} className="ui-cal-bar-cell">
                      <span
                        className="ui-cal-bar"
                        data-swatch={s.bar.swatch}
                        data-kind={s.bar.kind}
                        data-pending={s.bar.pending || undefined}
                        data-opens={opens || undefined}
                        data-closes={closes || undefined}
                      >
                        <bdi>{s.bar.label}</bdi>
                      </span>
                    </td>,
                  );
                  at = s.to;
                }
                if (at < 7) cells.push(<td key={`end-${at}`} colSpan={7 - at} />);
                return (
                  <tr key={i} className="ui-cal-bars">
                    {cells}
                  </tr>
                );
              })}
            </tbody>
          );
        })}
      </table>
    </div>
  );
}

const wide = "(min-width: 900px)";
const subscribeWide = (callback: () => void) => {
  const query = window.matchMedia(wide);
  query.addEventListener("change", callback);
  return () => query.removeEventListener("change", callback);
};

/** True at 900px and wider (DLS-073), false on the server. */
export function useWideLayout(): boolean {
  return useSyncExternalStore(subscribeWide, () => window.matchMedia(wide).matches, () => false);
}

/**
 * Day panel (DLS-073): beside the calendar at 900px and wider; a bottom sheet with a grab handle on smaller widths,
 * which keeps focus inside and closes on Escape.
 */
export function DayPanel({ open, onClose, title, children }: { open: boolean; onClose: () => void; title: string; children: ReactNode }) {
  const isWide = useWideLayout();
  const id = useId();
  if (!open) return null;
  if (isWide) {
    return (
      <aside className="ui-cal-panel" aria-labelledby={id}>
        <div className="ui-cal-panel-head">
          <h3 id={id}>{title}</h3>
          <CloseButton onClick={onClose} />
        </div>
        {children}
      </aside>
    );
  }
  return (
    <Drawer side="bottom" open={open} onClose={onClose} title={title}>
      <span className="ui-cal-grab" aria-hidden="true" />
      {children}
    </Drawer>
  );
}

export type HeatCell = { date: string; working: boolean; share: number | null; breach: boolean; detail: string };

/** One sequential step (1 to 5) for an available share; the text always says the number. */
const step = (share: number) => (share >= 90 ? 5 : share >= 75 ? 4 : share >= 60 ? 3 : share >= 40 ? 2 : 1);

/**
 * Team availability heatmap (DLS-074): one cell per day with the available share as text and as a colour step, a
 * warning icon and label under the threshold, and empty cells for non-working days, never 0%.
 */
export function AvailabilityHeatmap({
  label,
  cells,
  weekStart,
  breachLabel,
  nonWorkingLabel,
  shareLabel,
}: {
  label: string;
  cells: readonly HeatCell[];
  weekStart?: number;
  breachLabel: string;
  nonWorkingLabel: string;
  shareLabel: (share: number) => string;
}) {
  const locale = useLocale();
  const first = weekStart ?? firstWeekday(locale);
  const shortDay = new Intl.DateTimeFormat(locale, { weekday: "short", timeZone: "UTC" });
  const longDay = new Intl.DateTimeFormat(locale, { weekday: "long", timeZone: "UTC" });
  const number = new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", timeZone: "UTC" });
  if (cells.length === 0) return null;
  const lead = (new Date(fromIso(cells[0]!.date)).getUTCDay() - first + 7) % 7;
  const slots: (HeatCell | null)[] = [...Array<null>(lead).fill(null), ...cells];
  while (slots.length % 7 !== 0) slots.push(null);
  const rows = Array.from({ length: slots.length / 7 }, (_, i) => slots.slice(i * 7, i * 7 + 7));
  const headers = Array.from({ length: 7 }, (_, i) => addDays("2026-11-01", (first + i) % 7));
  return (
    <table className="ui-cal-heat" aria-label={label}>
      <thead>
        <tr>
          {headers.map((d) => (
            <th key={d} scope="col">
              <abbr title={longDay.format(fromIso(d))}>{shortDay.format(fromIso(d))}</abbr>
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row, r) => (
          <tr key={r}>
            {row.map((cell, i) =>
              cell === null ? (
                <td key={i} data-outside="true" />
              ) : (
                <td key={cell.date} data-step={cell.working && cell.share !== null ? step(cell.share) : undefined} data-breach={cell.breach || undefined} title={cell.detail}>
                  <span className="ui-cal-heat-date">{number.format(fromIso(cell.date))}</span>
                  {cell.working && cell.share !== null ? (
                    <span className="ui-cal-heat-share">
                      {cell.breach ? <Icon name="alert" size={12} label={breachLabel} /> : null}
                      {shareLabel(cell.share)}
                    </span>
                  ) : (
                    <span className="visually-hidden">{nonWorkingLabel}</span>
                  )}
                </td>
              ),
            )}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** Up to three stat cards (DLS-074): a value and a label each. */
export function CalendarStats({ items }: { items: readonly { key: string; value: string; label: string; tone?: "warning" }[] }) {
  return (
    <ul className="ui-cal-stats">
      {items.slice(0, 3).map((item) => (
        <li key={item.key} data-tone={item.tone}>
          <span className="ui-cal-stat-value">{item.value}</span>
          <span className="ui-cal-stat-label">{item.label}</span>
        </li>
      ))}
    </ul>
  );
}
