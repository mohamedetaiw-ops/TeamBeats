/**
 * Values in people's words (spec 036 DLS-043, docs/design-system/foundations/details.md): every language code, date,
 * time zone and time that reaches the screen goes through one of these, in the reader's locale. Nothing here holds a
 * business rule; the functions only change how a value the API gave is written.
 */

/** "en-US" becomes "English (United States)"; an unknown code is returned as it is. */
export function languageName(code: string, locale: string): string {
  try {
    return new Intl.DisplayNames([locale], { type: "language" }).of(code) ?? code;
  } catch {
    return code;
  }
}

/** A calendar date or timestamp in the long style ("1 November 2015"). A plain `YYYY-MM-DD` is read as that day, not as UTC midnight. */
export function dateLong(value: string, locale: string): string {
  const plain = /^\d{4}-\d{2}-\d{2}$/.test(value);
  const date = new Date(plain ? `${value}T12:00:00` : value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(locale, { dateStyle: "long" }).format(date);
}

/**
 * A `YYYY-MM-DD` day from a date field in words with the weekday ("Friday, 13 November 2026"), or null when it is not
 * a whole day. The browser's own date control follows the browser's language, so a date field writes this under it
 * (DLS-082).
 */
export function dayInWords(value: string | null | undefined, locale: string): string | null {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const date = new Date(`${value}T00:00:00Z`);
  if (Number.isNaN(date.getTime())) return null;
  return new Intl.DateTimeFormat(locale, { dateStyle: "full", timeZone: "UTC" }).format(date);
}

/** The full date and time, for a tooltip or the `title` of a relative time. */
export function dateTimeFull(value: string, locale: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(locale, { dateStyle: "full", timeStyle: "short" }).format(date);
}

/** The current time for a server render of relative times; a component passes it on so every row agrees. */
export function renderTime(): number {
  return Date.now();
}

const minute = 60_000;
const hour = 60 * minute;
const day = 24 * hour;

/**
 * A time in the last week as "now", "12 min ago", "3 hours ago" or "yesterday"; older times as a short date ("9 Nov",
 * with the year when it is not this year). `now` is passed in so a server render and the first client render agree.
 */
export function relativeTime(value: string, locale: string, now: number): string {
  const time = Date.parse(value);
  if (Number.isNaN(time)) return value;
  const diff = time - now;
  const ago = Math.abs(diff);
  const words = new Intl.RelativeTimeFormat(locale, { numeric: "auto", style: "short" });
  if (ago < minute) return words.format(0, "second");
  if (ago < hour) return words.format(Math.round(diff / minute), "minute");
  if (ago < day) return words.format(Math.round(diff / hour), "hour");
  if (ago < 7 * day) return words.format(Math.round(diff / day), "day");
  const sameYear = new Date(time).getFullYear() === new Date(now).getFullYear();
  return new Intl.DateTimeFormat(locale, sameYear ? { day: "numeric", month: "short" } : { dateStyle: "medium" }).format(time);
}

/** The offset of a time zone now, such as "GMT+02:00". */
function offset(zone: string, locale: string): string {
  try {
    const part = new Intl.DateTimeFormat(locale, { timeZone: zone, timeZoneName: "longOffset" })
      .formatToParts(new Date())
      .find((p) => p.type === "timeZoneName");
    return part?.value ?? "";
  } catch {
    return "";
  }
}

/** "Africa/Cairo" becomes "Cairo (GMT+02:00)"; the city comes from the zone name, the offset from the platform. */
export function timeZoneLabel(zone: string, locale: string): string {
  const city = zone.split("/").pop()?.replace(/_/g, " ") ?? zone;
  const off = offset(zone, locale);
  return off ? `${city} (${off})` : city;
}

/** The time zones the browser knows, for a picker; the saved zone is always in the list. */
export function timeZones(current: string | null): string[] {
  const all = typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : [];
  return current && !all.includes(current) ? [current, ...all] : all;
}

/** Joins separate facts with a middle dot, leaving out empty ones, so they never run together. */
export function joinMeta(parts: readonly (string | null | undefined | false)[]): string {
  return parts.filter((p): p is string => typeof p === "string" && p.trim() !== "").join(" · ");
}
