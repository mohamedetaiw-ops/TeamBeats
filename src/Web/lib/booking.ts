import { problemMessages } from "@/lib/notifications/types";

/** Workplace booking (spec 031): the shapes the API returns. Rules, limits and who may book are decided there. */
export const bookingApi = "/api/v1/booking";

export const bookingHref = "/booking";
export const myBookingsHref = "/booking/bookings";
export const bookingDetailHref = (id: string) => `/booking/bookings/${id}`;
export const manageBookingHref = "/booking/manage";
export const manageSiteHref = (id: string) => `/booking/manage/sites/${id}`;
export const siteLabelsHref = (id: string) => `/booking/manage/sites/${id}/labels`;
export const calendarFileHref = (id: string) => `${bookingApi}/bookings/${id}/calendar.ics`;
export const scanHref = (code: string) => `/booking/scan/${code}`;
export const kioskHref = (siteId: string) => `/booking/kiosk/${siteId}`;
export const qrImageHref = (resourceId: string) => `${bookingApi}/manage/resources/${resourceId}/qr.svg`;

export type ResourceKind = "room" | "desk" | "parking" | "equipment";
export const resourceKinds: ResourceKind[] = ["room", "desk", "parking", "equipment"];
export type Names = Record<string, string>;

/** The name in the viewer's language, else English, else any. */
export const pick = (names: Names | null | undefined, locale: string) => (names ? (names[locale] ?? names.en ?? Object.values(names)[0] ?? "") : "");

export type BookingRights = { book: boolean; manage: boolean; report: boolean; kinds: ResourceKind[] };

export type SiteView = {
  id: string;
  names: Names;
  locationKey: string | null;
  orgUnitId: string | null;
  timeZone: string;
  opensAt: string;
  closesAt: string;
  status: "active" | "removed";
  manages: boolean;
  version: number;
};

export type ZoneView = { id: string; names: Names };
export type FloorView = { id: string; names: Names; sort: number; zones: ZoneView[] };

export type ResourceView = {
  id: string;
  siteId: string;
  floorId: string | null;
  zoneId: string | null;
  kind: ResourceKind;
  names: Names;
  capacity: number | null;
  attributes: string[];
  fixedUserId: string | null;
  mailbox: string | null;
  checkIn: boolean;
  needsApproval: boolean;
  status: "active" | "removed";
  qrCode: string | null;
  version: number;
  approverIds: string[];
};

export type SiteDetail = { site: SiteView; floors: FloorView[]; resources: ResourceView[] };

export type QrLabel = { resourceId: string; kind: ResourceKind; names: Names; floor: Names | null; zone: Names | null; code: string; link: string };

export type SearchHit = { id: string; kind: ResourceKind; names: Names; capacity: number | null; attributes: string[]; floor: Names | null; zone: Names | null; needsApproval: boolean };
export type SearchResult = { siteId: string; timeZone: string; startsAt: string; endsAt: string; resources: SearchHit[] };

export type BookingStatus = "tentative" | "confirmed" | "checked_in" | "released" | "cancelled" | "rejected" | "expired";

export type BookingView = {
  id: string;
  resourceId: string;
  kind: ResourceKind;
  resourceNames: Names;
  siteId: string;
  siteNames: Names;
  timeZone: string;
  startsAt: string;
  endsAt: string;
  localDate: string;
  localStart: string;
  localEnd: string;
  status: BookingStatus;
  title: string | null;
  holderId: string;
  bookedBy: string;
  seriesId: string | null;
  checkedInAt: string | null;
  endReason: string | null;
  canCancel: boolean;
  version: number;
  /** A tentative booking waits for approval until then (FR-007). */
  holdUntil: string | null;
  /** The approver's reason when they rejected it. */
  decisionNote: string | null;
  checkInMethod: "qr" | "booking" | "kiosk" | null;
  /** The check-in window (FR-008), when the resource needs check-in. */
  checkInFrom: string | null;
  checkInUntil: string | null;
  canCheckIn: boolean;
};

/** What a scanned code shows: the resource, and the viewer's booking of it for now. */
export type ScanView = {
  resourceId: string;
  kind: ResourceKind;
  resourceNames: Names;
  siteId: string;
  siteNames: Names;
  timeZone: string;
  checkIn: boolean;
  booking: BookingView | null;
};

export type KioskView = { siteId: string; siteNames: Names; timeZone: string; bookings: BookingView[] };

export type TeamBookResult = { people: { personId: string; name: string | null; booking: BookingView | null; problem: string | null }[] };

export type BookResult = { bookings: BookingView[]; seriesId: string | null; skipped: { date: string; reason: "holiday" | "non_working_day" | "rules" }[] };

/** "10:00:00" as "10:00". */
export const hm = (time: string) => time.slice(0, 5);

/** Whether the site's time zone differs from the viewer's, so times are labelled with it (FR-017). */
export const otherZone = (timeZone: string) => {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone !== timeZone;
  } catch {
    return true;
  }
};

export async function send(url: string, method: "POST" | "PUT" | "DELETE", body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/** A call's result, or the API's messages (already in the viewer's language) when it was refused. */
export async function call<T>(request: () => Promise<Response>, failed: string): Promise<{ ok: true; value: T } | { ok: false; messages: string[] }> {
  try {
    const response = await request();
    if (response.ok) return { ok: true, value: (response.status === 204 ? undefined : await response.json()) as T };
    return { ok: false, messages: await problemMessages(response) };
  } catch {
    return { ok: false, messages: [failed] };
  }
}

/** The rule a resource follows, and where it comes from: its own, its kind at the site, its kind in the tenant, or the defaults. */
export type RuleView = {
  source: "resource" | "site" | "tenant" | "default";
  unitIds: string[];
  advanceDays: number;
  minMinutes: number;
  maxMinutes: number;
  workingHours: boolean;
  bufferMinutes: number;
  maxActive: number;
  overnight: boolean;
  seriesDays: number;
  version: number;
};

/** Tomorrow's date, the first day the search offers; the site's own day is checked by the API. */
export function tomorrowIso(now: number = Date.now()): string {
  return new Date(now + 86_400_000).toISOString().slice(0, 10);
}

// ---- Usage reports (spec 031 US6, FR-014): counts and hours only ----

export const bookingReportsHref = "/booking/reports";
export const reportGroups = ["resource", "zone", "site", "week"] as const;
export type ReportGroup = (typeof reportGroups)[number];
export type ReportSite = { id: string; names: Names; timeZone: string; status: string };
export type UsageRow = {
  key: string;
  names: Names | null;
  weekStart: string | null;
  resources: number;
  bookings: number;
  checkedIn: number;
  noShows: number;
  cancelled: number;
  bookedHours: number;
  usedHours: number;
  availableHours: number;
  utilization: number | null;
  noShowRate: number | null;
};
export type UsageReport = {
  siteId: string;
  siteNames: Names;
  timeZone: string;
  from: string;
  to: string;
  groupBy: ReportGroup;
  total: UsageRow;
  rows: UsageRow[];
};

/** The last four whole weeks up to today, as ISO dates. */
export function lastWeeksIso(now: number = Date.now()): { from: string; to: string } {
  return { from: new Date(now - 27 * 86_400_000).toISOString().slice(0, 10), to: new Date(now).toISOString().slice(0, 10) };
}

// ---- Closed days (spec 031 section 4 edge case) ----

export type ClosureView = { siteId: string; date: string; reason: string; closedAt: string; cancelled: number };

// ---- The day of one resource (spec 031 section 10 time grid) ----

export type BusySlot = { startsAt: string; endsAt: string; status: string; holder: string | null };
export type AvailabilityView = { resourceId: string; date: string; timeZone: string; opensAt: string; closesAt: string; workingDay: boolean; busy: BusySlot[] };

/** Minutes after midnight on <paramref name="date"/> in the site's time zone, clamped to the day. */
export function minutesOnDay(iso: string, date: string, timeZone: string): number {
  const parts = new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23" })
    .formatToParts(new Date(iso));
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "00";
  const day = `${get("year")}-${get("month")}-${get("day")}`;
  if (day < date) return 0;
  if (day > date) return 24 * 60;
  return Number(get("hour")) * 60 + Number(get("minute"));
}

/** "HH:mm" for minutes after midnight. */
export const clock = (minutes: number) => `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;
