import type { PersonRef } from "@/lib/leave";

/** HR letters (spec 022 US7, FR-021, FR-022): the shapes the API returns. Rules live in the API. */
export type LetterStatus = "pending" | "approved" | "issued" | "rejected" | "withdrawn";
export type LetterLanguage = "en" | "ar";
export type Names = Record<string, string>;

export type LetterChoice = { key: string; names: Names; needsApproval: boolean };

export type LetterField = { key: string; names: Names | null; value: string | null; fromHr: boolean };

export type Letter = {
  id: string;
  reference: string;
  typeKey: string;
  typeNames: Names;
  language: LetterLanguage;
  addressee: string | null;
  person: PersonRef;
  status: LetterStatus;
  createdAt: string;
  decidedAt: string | null;
  issuedAt: string | null;
  fields: LetterField[] | null;
  rights: { withdraw: boolean; decide: boolean; editValues: boolean; download: boolean };
};

export type LetterManualField = { key: string; names: Names };

export type LetterType = {
  id: string;
  key: string;
  names: Names;
  templates: Names;
  manualFields: LetterManualField[];
  needsApproval: boolean;
  orgUnitId: string | null;
  active: boolean;
  version: number;
  updatedAt: string;
  canEdit: boolean;
  eTag: string;
};

export type LetterSettings = { verificationEnabled: boolean; version: number; canChange: boolean; eTag: string };

export type LetterVerification = { reference: string; typeNames: Names; issuedOn: string; companyName: string };

/** Placeholders a template may use besides the type's own fields; the API checks them again. */
export const LETTER_PLACEHOLDERS = ["name", "employee_number", "job_title", "grade", "hire_date", "company", "addressee", "date", "reference"] as const;

export const letterTone: Record<LetterStatus, "neutral" | "info" | "success" | "danger" | "warning"> = {
  pending: "info",
  approved: "warning",
  issued: "success",
  rejected: "danger",
  withdrawn: "neutral",
};
