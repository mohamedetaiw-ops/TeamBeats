import type { IconName } from "@/components/ui/Icon";
import { addDays, fromIso, weekdayOf } from "@/components/ui/dates";

/** The layers of the unified calendar (spec 037 section 2), in their display order. The API says which apply. */
export const LAYERS = ["meeting", "company", "department", "important", "holiday", "my_leave", "team_leave"] as const;
export type Layer = (typeof LAYERS)[number];

/** Each layer's icon and chart colour step (spec 036 DLS-070). Presentation only: the API decides what each viewer gets. */
export const LAYER_LOOK: Record<Layer, { icon: IconName; swatch: number }> = {
  meeting: { icon: "video", swatch: 1 },
  company: { icon: "megaphone", swatch: 2 },
  department: { icon: "building", swatch: 3 },
  important: { icon: "star", swatch: 4 },
  holiday: { icon: "sun", swatch: 5 },
  my_leave: { icon: "leave", swatch: 6 },
  team_leave: { icon: "people", swatch: 7 },
};

export const VIEWS = ["agenda", "week", "month", "team"] as const;
export type CalendarView = (typeof VIEWS)[number];

export type ItemAction = "join" | "open_source" | "rsvp" | "open_leave" | "request_leave" | "view_team";
export type ItemStatus = "confirmed" | "tentative" | "pending" | "cancelled";
export type Rsvp = { answer: "going" | "maybe" | "declined" | null; open: boolean; going: number | null; maybe: number | null; declined: number | null };

/** The canonical item of spec 037 FR-001: timed items have UTC instants, all-day items dates with an exclusive end. */
export type CalendarItem = {
  id: string;
  layer: Layer;
  source: string;
  sourceId: string;
  title: string | null;
  allDay: boolean;
  start: string | null;
  end: string | null;
  startDate: string | null;
  endDate: string | null;
  timeZone: string;
  status: ItemStatus;
  visibility: string;
  actions: ItemAction[];
  location: string | null;
  onlineUrl: string | null;
  webLink: string | null;
  description: string | null;
  free: boolean;
  isPrivate: boolean;
  halfStart: boolean;
  halfEnd: boolean;
  count: number | null;
  rsvp: Rsvp | null;
  occurrenceKey: string | null;
  recurring: boolean;
  leaveRequestId: string | null;
};

export type SourceStatus = { source: string; state: string; asOf: string | null };

export type EventsAnswer = { timeZone: string; from: string; to: string; layers: Layer[]; sources: SourceStatus[]; items: CalendarItem[]; nextCursor: string | null };

export type DaySummary = { date: string; layers: Partial<Record<Layer, number>>; teamAway: number };

export type SummaryAnswer = {
  timeZone: string;
  today: string;
  from: string;
  to: string;
  available: Layer[];
  enabled: Layer[];
  view: CalendarView;
  isManager: boolean;
  days: DaySummary[];
  alerts: { holidays: { date: string; title: string; halfDay: boolean }[]; teamAwayToday: number };
  sources: SourceStatus[];
};

export type Preferences = { available: Layer[]; layers: Layer[]; view: CalendarView };

export type TeamDay = { date: string; working: boolean; total: number; away: number; available: number; share: number | null; breach: boolean };
export type TeamRange = { start: string; end: string; status: "approved" | "pending"; halfStart: boolean; halfEnd: boolean; typeKey: string | null; typeName: string | null; requestId: string | null };
export type TeamAnswer = {
  timeZone: string;
  from: string;
  to: string;
  today: string;
  teamSize: number;
  threshold: number;
  indirectReports: boolean;
  todayCounts: { available: number; total: number; onLeave: number; startingSoon: number };
  days: TeamDay[];
  people: { userId: string; displayName: string; leave: TeamRange[] }[];
  upcoming: { userId: string; displayName: string; start: string; end: string; status: "approved" | "pending"; typeName: string | null }[];
  more: number;
};

export type Settings = { version: number; indirectReports: boolean; threshold: number; defaultLayers: Layer[]; updatedAt: string | null };

export type Names = Record<string, string>;
export type Recurrence = { frequency: "daily" | "weekly" | "monthly"; interval: number; weekdays: number[] | null; until: string | null; count: number | null };
export type AdminEvent = {
  id: string;
  orgUnitId: string | null;
  kind: "company" | "department" | "important";
  titles: Names;
  descriptions: Names;
  allDay: boolean;
  start: string | null;
  end: string | null;
  startDate: string | null;
  endDate: string | null;
  timeZone: string;
  location: string | null;
  onlineUrl: string | null;
  recurrence: Recurrence | null;
  rsvp: boolean;
  status: "published" | "cancelled";
  cancelledAt: string | null;
  version: number;
  eTag: string;
};

/** The local day (yyyy-mm-dd) of an instant in a zone. */
export function zoneDay(instant: string | number, zone: string): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date(instant));
}

/** Whether an item touches a local day, by the same rule as the API: all-day by dates, timed by instants in the zone. */
export function touches(item: CalendarItem, day: string, zone: string): boolean {
  if (item.allDay) return item.startDate! <= day && day < item.endDate!;
  const start = zoneDay(item.start!, zone);
  let end = zoneDay(item.end!, zone);
  const endTime = new Intl.DateTimeFormat("en-GB", { timeZone: zone, hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(new Date(item.end!));
  if (endTime === "00:00" && end > start) end = addDays(end, -1);
  return start <= day && day <= end;
}

/** The first day of the week that holds `day`, for a week start (0 for Sunday). */
export function weekOf(day: string, weekStart: number): string {
  return addDays(day, -((weekdayOf(day) - weekStart + 7) % 7));
}

export function daysFrom(start: string, count: number): string[] {
  return Array.from({ length: count }, (_, i) => addDays(start, i));
}

export const isoDay = (value: string | null): value is string => typeof value === "string" && /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(fromIso(value));

/** Whether Join is the primary action now: from 10 minutes before an online item starts until it ends (spec 018). */
export function liveNow(item: CalendarItem, now: number): boolean {
  if (!item.onlineUrl || !item.start || !item.end || !item.actions.includes("join")) return false;
  return now >= Date.parse(item.start) - 10 * 60_000 && now < Date.parse(item.end);
}
