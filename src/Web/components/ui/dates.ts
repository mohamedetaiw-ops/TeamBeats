/**
 * Calendar arithmetic for the date components (spec 036 DLS-036). A date is an ISO day, "2026-11-10", with no time and
 * no zone. Months and years are counted in the calendar the tenant uses: the Gregorian one or the Hijri (Umm al-Qura)
 * one. `Intl` knows both, so nothing here holds calendar tables: a month's days are found by asking `Intl` which
 * month each day falls in.
 */
export type CalendarSystem = "gregory" | "islamic-umalqura";
export type DateRange = { start: string | null; end: string | null };

const day = 86_400_000;
const partFormats = new Map<CalendarSystem, Intl.DateTimeFormat>();

export const fromIso = (iso: string) => Date.parse(`${iso}T00:00:00Z`);
export const toIso = (ms: number) => new Date(ms).toISOString().slice(0, 10);
export const addDays = (iso: string, count: number) => toIso(fromIso(iso) + count * day);
/** Day of the week, 0 for Sunday. */
export const weekdayOf = (iso: string) => new Date(fromIso(iso)).getUTCDay();
/** The viewer's own day for a moment in time. */
export const localIso = (ms: number) => {
  const d = new Date(ms);
  return toIso(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
};

/** Year, month and day of an ISO day in the given calendar. */
export function calendarParts(iso: string, calendar: CalendarSystem): { year: number; month: number; day: number } {
  let format = partFormats.get(calendar);
  if (!format) {
    format = new Intl.DateTimeFormat(`en-u-ca-${calendar}-nu-latn`, { year: "numeric", month: "numeric", day: "numeric", timeZone: "UTC" });
    partFormats.set(calendar, format);
  }
  const parts = format.formatToParts(fromIso(iso));
  const number = (type: string) => Number(parts.find((p) => p.type === type)?.value);
  return { year: number("year"), month: number("month"), day: number("day") };
}

export const monthStart = (iso: string, calendar: CalendarSystem) => addDays(iso, 1 - calendarParts(iso, calendar).day);

/** Days in the month that starts on `start`: 28 to 31 in the Gregorian calendar, 29 or 30 in the Hijri one. */
export function monthLength(start: string, calendar: CalendarSystem): number {
  for (let n = 28; n <= 31; n += 1) {
    if (calendarParts(addDays(start, n), calendar).day === 1) return n;
  }
  return 31;
}

/** The same day of the month `count` months away, or that month's last day when it is shorter. */
export function addMonths(iso: string, count: number, calendar: CalendarSystem): string {
  const wanted = calendarParts(iso, calendar).day;
  let start = monthStart(iso, calendar);
  for (let i = 0; i < Math.abs(count); i += 1) {
    start = count > 0 ? addDays(start, monthLength(start, calendar)) : monthStart(addDays(start, -1), calendar);
  }
  return addDays(start, Math.min(wanted, monthLength(start, calendar)) - 1);
}

/** The weeks of a month as rows of seven cells; cells outside the month are null. `weekStart` is 0 for Sunday. */
export function monthGrid(start: string, calendar: CalendarSystem, weekStart: number): (string | null)[][] {
  const length = monthLength(start, calendar);
  const lead = (weekdayOf(start) - weekStart + 7) % 7;
  const cells: (string | null)[] = [...Array<null>(lead).fill(null), ...Array.from({ length }, (_, i) => addDays(start, i))];
  while (cells.length % 7 !== 0) cells.push(null);
  return Array.from({ length: cells.length / 7 }, (_, week) => cells.slice(week * 7, week * 7 + 7));
}

/** The first day of the week in a locale, 0 for Sunday. Falls back to Monday where the browser cannot say. */
export function firstWeekday(locale: string): number {
  try {
    const intl = new Intl.Locale(locale) as Intl.Locale & { getWeekInfo?: () => { firstDay: number }; weekInfo?: { firstDay: number } };
    const info = intl.getWeekInfo?.() ?? intl.weekInfo;
    if (info) return info.firstDay % 7;
  } catch {
    // An unknown locale tag: use the fallback.
  }
  return 1;
}

export const inRange = (iso: string, min?: string, max?: string) => (!min || iso >= min) && (!max || iso <= max);
