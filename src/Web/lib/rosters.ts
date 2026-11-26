import type { PersonRef } from "@/lib/leave";

/** Shift rosters (spec 022 US10, FR-029): the shapes the API returns. Rules live in the API. */
export type ShiftTone = "neutral" | "info" | "success" | "warning" | "danger";
export const SHIFT_TONES: readonly ShiftTone[] = ["neutral", "info", "success", "warning", "danger"];

export type ShiftPattern = {
  id: string;
  names: Record<string, string>;
  start: string;
  end: string;
  breakMinutes: number;
  tone: ShiftTone;
  active: boolean;
  version: number;
};

export type ShiftPatterns = { patterns: ShiftPattern[]; canManage: boolean };

export type RosterDay = { date: string; patternId: string | null; dayOff: boolean; leave: boolean; holiday: boolean; conflict: boolean };

export type RosterPerson = { person: PersonRef; days: RosterDay[] };

export type RosterWeek = {
  id: string | null;
  manager: PersonRef;
  weekStart: string;
  status: "new" | "draft" | "published";
  version: number;
  changed: boolean;
  publishedAt: string | null;
  people: RosterPerson[];
  canEdit: boolean;
};

export type MyShift = { date: string; pattern: ShiftPattern | null; dayOff: boolean };

export const patternName = (p: ShiftPattern, locale: string) => p.names[locale] ?? p.names.en ?? "";

/** The Monday of the week holding the date, as an ISO date (the planner's default week). */
export function weekOf(iso: string): string {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() - ((d.getUTCDay() + 6) % 7));
  return d.toISOString().slice(0, 10);
}

export function addDays(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
