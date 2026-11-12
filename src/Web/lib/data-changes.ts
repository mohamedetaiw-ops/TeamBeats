import type { PersonRef } from "@/lib/leave";

/** Employee data change requests (spec 022 US6, FR-019, FR-020): the shapes the API returns. Rules live in the API. */
export type DataChangeStatus = "pending" | "approved" | "completed" | "write_failed" | "rejected" | "withdrawn";

export type DataChangeField = { key: string; maxLength: number; required: boolean; choices: string[] | null };

export type DataChangeGroup = { key: string; fields: DataChangeField[]; sensitive: boolean; enabled: boolean; document: boolean };

export type DataChangeSettings = { groups: DataChangeGroup[]; version: number; canChange: boolean; eTag: string };

export type DataChange = {
  id: string;
  group: string;
  person: PersonRef;
  fields: { key: string; value: string | null }[];
  reason: string | null;
  documentName: string | null;
  status: DataChangeStatus;
  writeBack: "hr" | "manual" | null;
  externalReference: string | null;
  createdAt: string;
  decidedAt: string | null;
  completedAt: string | null;
  rights: { withdraw: boolean; decide: boolean; complete: boolean; document: boolean };
};

export const dataChangeTone: Record<DataChangeStatus, "neutral" | "info" | "success" | "danger" | "warning"> = {
  pending: "info",
  approved: "warning",
  completed: "success",
  write_failed: "danger",
  rejected: "danger",
  withdrawn: "neutral",
};
