/** Shapes of the spec 022 calendar API, as the browser reads them. Week days are ISO numbers, 1 for Monday to 7 for Sunday. */
export type Holiday = { date: string; name: Record<string, string>; halfDay: boolean };

/** A reduced hours period such as Ramadan: working days from start to end have this many working hours. */
export type Period = { id: string; name: Record<string, string>; start: string; end: string; hours: number };

export type Calendar = {
  id: string;
  name: Record<string, string>;
  country: string | null;
  weekDays: number[];
  ownerOrgUnitId: string | null;
  isTenantDefault: boolean;
  version: number;
  eTag: string;
  assignedUnits: string[];
  canEdit: boolean;
  holidays: Holiday[] | null;
  periods: Period[] | null;
};

export type Assignment = { orgUnitId: string; calendarId: string };

export type WorkingDaysPreview = { calendarId: string | null; from: string; to: string; workingDays: number; days: { date: string; fraction: number; reducedHours: number | null }[] };

export const isoWeek = [1, 2, 3, 4, 5, 6, 7] as const;

/** The weekday name in the viewer's language for an ISO day number, from the browser's own calendar data. */
export function weekdayName(iso: number, locale: string): string {
  // 5 January 2026 is a Monday.
  return new Intl.DateTimeFormat(locale, { weekday: "long", timeZone: "UTC" }).format(new Date(Date.UTC(2026, 0, 4 + iso)));
}
