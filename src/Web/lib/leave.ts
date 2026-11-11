/** Leave (spec 022 R2 slice 1). The API decides every rule: balances, working days, overlap, who may see a leave type and who may act. */
export type Names = Record<string, string>;

export type LeaveUnit = "day" | "hour";

export type LeaveType = {
  id: string;
  key: string;
  names: Names;
  unit: LeaveUnit;
  halfDays: boolean;
  paid: boolean;
  sensitive: boolean;
  documentAfterDays: number | null;
  noticeDays: number;
  maxDaysPerRequest: number | null;
  negativeAllowed: boolean;
  negativeMax: number;
  membershipTypes: string[] | null;
  carryOverMax: number;
  yearStartMonth: number;
  cancelNeedsApproval: boolean;
  active: boolean;
  version: number;
  eTag: string;
};

export type LeaveBand = { days: number; minServiceYears?: number | null; grades?: string[] | null };

export type LeavePolicy = { id: string; typeKey: string; orgUnitId: string | null; bands: LeaveBand[]; accrual: "monthly" | "yearly"; version: number; canEdit: boolean; eTag: string };

export type LeaveBalance = { typeKey: string; typeNames: Names; leaveYear: number; balance: number; pending: number; available: number; unit: LeaveUnit };

export type PersonRef = { id: string; displayName: string };

export type OverlapPerson = { userId: string; displayName: string; dates: string[]; status: "approved" | "pending"; typeKey: string | null };

export type YearBalance = { leaveYear: number; days: number; balance: number; pending: number; available: number; after: number };

export type LeavePreview = {
  typeKey: string;
  unit: LeaveUnit;
  days: number;
  dates: string[];
  years: YearBalance[];
  documentRequired: boolean;
  overlap: OverlapPerson[];
  problems: { key: string; message: string }[];
};

export type LeaveStatus = "pending" | "approved" | "rejected" | "cancelled" | "withdrawn";

export type LeaveRights = { cancel: boolean; shorten: boolean; answerReplacement: boolean; nameReplacement: boolean; decide: boolean };

export type LeaveRequest = {
  id: string;
  number: string;
  person: PersonRef;
  filedBy: PersonRef;
  typeKey: string;
  typeNames: Names;
  unit: LeaveUnit;
  start: string;
  end: string;
  startHalf: boolean;
  endHalf: boolean;
  days: number;
  status: LeaveStatus;
  comment: string | null;
  replacement: PersonRef | null;
  replacementAnswer: "accepted" | "declined" | null;
  hasDocument: boolean;
  changeKind: "cancel" | "shorten" | null;
  changeEnd: string | null;
  createdAt: string;
  decidedAt: string | null;
  rights: LeaveRights;
  overlap: OverlapPerson[] | null;
};

export type MyLeave = { balances: LeaveBalance[]; types: LeaveType[]; requests: LeaveRequest[]; replacementAsks: LeaveRequest[] };

export type LeaveAbsence = { userId: string; date: string; fraction: number; status: "approved" | "pending"; typeKey: string | null };

export type TeamAvailability = { from: string; to: string; people: { userId: string; displayName: string }[]; absences: LeaveAbsence[] };

/** Dates as the API sends them (yyyy-mm-dd), handled in UTC so a calendar date never moves with the browser's zone. */
export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

export function datesBetween(from: string, to: string): string[] {
  const list: string[] = [];
  for (let d = from; d <= to && list.length < 400; d = addDays(d, 1)) list.push(d);
  return list;
}

export function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

export const statusTone: Record<LeaveStatus, "neutral" | "info" | "success" | "warning" | "danger"> = {
  pending: "info",
  approved: "success",
  rejected: "danger",
  cancelled: "neutral",
  withdrawn: "neutral",
};
