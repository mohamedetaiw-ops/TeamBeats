"use client";

import { useId, type CSSProperties } from "react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import type { BadgeTone } from "./Badge";
import { addDays, addMonths, firstWeekday, fromIso, monthGrid, monthStart, type CalendarSystem } from "./dates";
import { IconButton } from "./IconButton";

export type CalendarEvent = {
  id: string;
  title: string;
  /** The day, as an ISO date in the viewer's time zone; the feature converts. */
  day: string;
  /** Local times as "HH:MM". An event without them lasts all day. */
  start?: string;
  end?: string;
  tone?: Exclude<BadgeTone, "neutral">;
  /** A chart colour step (1 to 8) for a calendar layer (DLS-070); it wins over the tone. */
  swatch?: number;
  href?: string;
};

const minutes = (time: string) => {
  const [h, m] = time.split(":").map(Number);
  return (h ?? 0) * 60 + (m ?? 0);
};

function useTimes(locale: string) {
  const format = new Intl.DateTimeFormat(locale, { hour: "numeric", minute: "2-digit", timeZone: "UTC" });
  return (day: string, time: string) => format.format(new Date(`${day}T${time}:00Z`));
}

function EventChip({ event, time }: { event: CalendarEvent; time?: string }) {
  const content = (
    <>
      <bdi className="ui-event-title">{event.title}</bdi>
      {time ? <span className="ui-event-time">{time}</span> : null}
    </>
  );
  return event.href ? (
    <Link className="ui-event" data-tone={event.tone} data-swatch={event.swatch} href={event.href}>
      {content}
    </Link>
  ) : (
    <span className="ui-event" data-tone={event.tone} data-swatch={event.swatch}>
      {content}
    </span>
  );
}

/**
 * Month view (spec 036 DLS-039): a month of events as a table with the weekday names as column headers, in the
 * tenant's calendar and the viewer's first day of the week. Each day lists up to `limit` events and says how many more
 * there are. Moving between months is the feature's, through `onMonthChange`.
 */
export function MonthView({
  month,
  events,
  today,
  calendar = "gregory",
  weekStart,
  limit = 3,
  onMonthChange,
}: {
  /** Any day in the month to show. */
  month: string;
  events: readonly CalendarEvent[];
  today?: string;
  calendar?: CalendarSystem;
  weekStart?: number;
  limit?: number;
  onMonthChange?: (month: string) => void;
}) {
  const locale = useLocale();
  const t = useTranslations("ui");
  const id = useId();
  const time = useTimes(locale);
  const start = monthStart(month, calendar);
  const first = weekStart ?? firstWeekday(locale);
  const weeks = monthGrid(start, calendar, first);
  const title = new Intl.DateTimeFormat(`${locale}-u-ca-${calendar}`, { month: "long", year: "numeric", timeZone: "UTC" }).format(fromIso(start));
  const dayNumber = new Intl.DateTimeFormat(`${locale}-u-ca-${calendar}`, { day: "numeric", timeZone: "UTC" });
  const longDay = new Intl.DateTimeFormat(locale, { weekday: "long", timeZone: "UTC" });
  const shortDay = new Intl.DateTimeFormat(locale, { weekday: "short", timeZone: "UTC" });
  const fullDate = new Intl.DateTimeFormat(`${locale}-u-ca-${calendar}`, { dateStyle: "full", timeZone: "UTC" });
  // 2026-11-01 is a Sunday, so day `n` of that week is weekday `n`.
  const weekdays = Array.from({ length: 7 }, (_, i) => addDays("2026-11-01", (first + i) % 7));
  const byDay = new Map<string, CalendarEvent[]>();
  for (const e of [...events].sort((a, b) => (a.start ?? "").localeCompare(b.start ?? ""))) byDay.set(e.day, [...(byDay.get(e.day) ?? []), e]);

  return (
    <div className="ui-month">
      <div className="ui-month-head">
        <h3 id={`${id}-title`}>{title}</h3>
        {onMonthChange ? (
          <div className="ui-month-nav">
            <IconButton icon="chevron-forward" className="ui-month-previous" label={t("controls.previousMonth")} onClick={() => onMonthChange(addMonths(start, -1, calendar))} />
            <IconButton icon="chevron-forward" label={t("controls.nextMonth")} onClick={() => onMonthChange(addMonths(start, 1, calendar))} />
          </div>
        ) : null}
      </div>
      <div className="ui-table-scroll">
        <table aria-labelledby={`${id}-title`}>
          <thead>
            <tr>
              {weekdays.map((d) => (
                <th key={d} scope="col">
                  <abbr title={longDay.format(fromIso(d))}>{shortDay.format(fromIso(d))}</abbr>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {weeks.map((week, w) => (
              <tr key={w}>
                {week.map((day, i) => {
                  if (!day) return <td key={i} data-outside="true" />;
                  const list = byDay.get(day) ?? [];
                  const extra = list.length - limit;
                  return (
                    <td key={day} data-today={day === today || undefined}>
                      <span className="ui-month-day" aria-current={day === today ? "date" : undefined}>
                        <span aria-hidden="true">{dayNumber.format(fromIso(day))}</span>
                        <span className="visually-hidden">{fullDate.format(fromIso(day))}</span>
                      </span>
                      {list.length ? (
                        <ul>
                          {list.slice(0, limit).map((e) => (
                            <li key={e.id}>
                              <EventChip event={e} time={e.start ? time(day, e.start) : undefined} />
                            </li>
                          ))}
                          {extra > 0 ? <li className="ui-month-more">{t("data.more", { count: extra })}</li> : null}
                        </ul>
                      ) : null}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** Puts overlapping events side by side: each gets a lane, and every event in a cluster knows how many lanes it has. */
function lanes(list: readonly CalendarEvent[]) {
  const out = new Map<string, { lane: number; lanes: number }>();
  let cluster: { id: string; lane: number }[] = [];
  let ends: number[] = [];
  let clusterEnd = -1;
  const close = () => {
    for (const c of cluster) out.set(c.id, { lane: c.lane, lanes: ends.length });
    cluster = [];
    ends = [];
  };
  for (const e of [...list].sort((a, b) => minutes(a.start!) - minutes(b.start!))) {
    const s = minutes(e.start!);
    const f = minutes(e.end ?? e.start!);
    if (s >= clusterEnd) close();
    let lane = ends.findIndex((end) => end <= s);
    if (lane < 0) lane = ends.push(f) - 1;
    else ends[lane] = f;
    cluster.push({ id: e.id, lane });
    clusterEnd = Math.max(clusterEnd, f);
  }
  close();
  return out;
}

/**
 * Week view (spec 036 DLS-039): days side by side with timed events placed by their start and end, all-day events on
 * top, and overlapping events sharing the width. Each day is a section with its own heading and an ordered list, so a
 * screen reader hears the day, then each event with its time.
 */
export function WeekView({
  start,
  days = 7,
  events,
  today,
  startHour = 8,
  endHour = 18,
}: {
  /** The first day to show, as an ISO date. */
  start: string;
  days?: number;
  events: readonly CalendarEvent[];
  today?: string;
  startHour?: number;
  endHour?: number;
}) {
  const locale = useLocale();
  const t = useTranslations("ui");
  const id = useId();
  const time = useTimes(locale);
  const dayName = new Intl.DateTimeFormat(locale, { weekday: "short", day: "numeric", timeZone: "UTC" });
  const hour = new Intl.DateTimeFormat(locale, { hour: "numeric", timeZone: "UTC" });
  const list = Array.from({ length: days }, (_, i) => addDays(start, i));
  const slots = (endHour - startHour) * 2;
  const row = (time: string) => Math.max(1, Math.min(slots + 1, Math.round((minutes(time) - startHour * 60) / 30) + 1));

  return (
    <div className="ui-week" style={{ "--xp-days": days, "--xp-slots": slots } as CSSProperties}>
      <div className="ui-week-hours" aria-hidden="true">
        {Array.from({ length: endHour - startHour - 1 }, (_, i) => (
          <span key={i} style={{ gridRow: i * 2 + 5 }}>
            {hour.format(Date.UTC(2026, 0, 1, startHour + i + 1))}
          </span>
        ))}
      </div>
      {list.map((day) => {
        const all = events.filter((e) => e.day === day && !e.start);
        const timed = events.filter((e) => e.day === day && e.start).sort((a, b) => minutes(a.start!) - minutes(b.start!));
        const placed = lanes(timed);
        return (
          <section key={day} className="ui-week-day" aria-labelledby={`${id}-${day}`} data-today={day === today || undefined}>
            <h4 id={`${id}-${day}`} aria-current={day === today ? "date" : undefined}>
              {dayName.format(fromIso(day))}
            </h4>
            <ul className="ui-week-all" aria-label={t("data.allDay")}>
              {all.map((e) => (
                <li key={e.id}>
                  <EventChip event={e} />
                </li>
              ))}
            </ul>
            <ol className="ui-week-events">
              {timed.map((e) => {
                const at = placed.get(e.id) ?? { lane: 0, lanes: 1 };
                const label = t("data.timeRange", { start: time(day, e.start!), end: time(day, e.end ?? e.start!) });
                return (
                  <li
                    key={e.id}
                    style={{ gridRow: `${row(e.start!)} / ${Math.max(row(e.end ?? e.start!), row(e.start!) + 1)}`, "--xp-lane": at.lane, "--xp-lanes": at.lanes } as CSSProperties}
                  >
                    <EventChip event={e} time={label} />
                  </li>
                );
              })}
            </ol>
          </section>
        );
      })}
    </div>
  );
}
