/** Attendance (spec 022 R2 slice 4). The API decides every rule: who may sign in, the location check, the day's hours and exceptions, and who may see whom. */
import type { PersonRef } from "@/lib/leave";

export type AttendanceKind = "working" | "day_off" | "holiday" | "leave";

export const ATTENDANCE_FLAGS = ["late", "early_leave", "missing_punch", "absent", "remote", "worked_day_off"] as const;
export type AttendanceFlag = (typeof ATTENDANCE_FLAGS)[number];

export type Punch = { id: string; at: string; direction: "in" | "out"; remote: boolean; source: "web" | "clock" };

export type AttendanceDay = {
  date: string;
  kind: AttendanceKind;
  firstIn: string | null;
  lastOut: string | null;
  hoursWorked: number;
  expectedHours: number;
  flags: AttendanceFlag[];
  punches: Punch[];
};

export type AttendanceTotals = {
  workedDays: number;
  hoursWorked: number;
  expectedHours: number;
  late: number;
  earlyLeave: number;
  missingPunch: number;
  absent: number;
  remote: number;
  workedDaysOff: number;
};

export type WorkRule = {
  id: string;
  orgUnitId: string | null;
  start: string;
  end: string;
  graceLateMinutes: number;
  graceEarlyMinutes: number;
  version: number;
  updatedAt: string;
  eTag: string;
};

export type LocationMode = "off" | "flag" | "refuse";

export type AttendanceMonth = {
  person: PersonRef;
  month: string;
  rule: WorkRule | null;
  days: AttendanceDay[];
  totals: AttendanceTotals;
  webSignIn: boolean;
  locationMode: LocationMode;
  lastPunch: Punch | null;
  mine: boolean;
};

export type TeamAttendance = { month: string; people: { person: PersonRef; totals: AttendanceTotals }[] };

export type AttendanceSite = { name: string; latitude: number; longitude: number; radiusMeters: number };

export type AttendanceSettings = {
  webSignIn: boolean;
  locationMode: LocationMode;
  sites: AttendanceSite[];
  version: number;
  updatedAt: string | null;
  canChange: boolean;
  eTag: string;
};

/** Months as the API takes them (yyyy-mm), counted in UTC so a month never moves with the browser's zone. */
export function thisMonth(): string {
  return new Date().toISOString().slice(0, 7);
}

export function addMonths(month: string, delta: number): string {
  const y = Number(month.slice(0, 4));
  const m = Number(month.slice(5, 7));
  const d = new Date(Date.UTC(y, m - 1 + delta, 1));
  return d.toISOString().slice(0, 7);
}

export const flagTone: Record<AttendanceFlag, "neutral" | "info" | "warning" | "danger" | "accent"> = {
  late: "warning",
  early_leave: "warning",
  missing_punch: "danger",
  absent: "danger",
  remote: "info",
  worked_day_off: "accent",
};

/** The person's position, asked only when the tenant turned the location check on, and only for the sign in itself. */
export function currentPosition(): Promise<{ latitude: number; longitude: number } | null> {
  if (typeof navigator === "undefined" || !navigator.geolocation) return Promise.resolve(null);
  return new Promise((resolve) => {
    navigator.geolocation.getCurrentPosition(
      (p) => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude }),
      () => resolve(null),
      { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 },
    );
  });
}
