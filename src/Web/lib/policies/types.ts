import type { BodyDocument, ContentAudience } from "@/lib/content/types";

/** Shapes of the policy API (spec 025). The page only shows what these say; the API decides who may do what. */
export type Names = Record<string, string>;

export type PolicyPerson = { id: string; displayName: string };

export type ClauseView = { key: string; anchor: string; number: string; level: number; titles: Names; bodies: Record<string, BodyDocument> };

export type ApprovalView = { step: string; stepNames: Names | null; approver: PolicyPerson; onBehalfOf: PolicyPerson | null; at: string };

export type VersionView = {
  id: string;
  number: number | null;
  state: "draft" | "in_review" | "approved" | "effective" | "superseded";
  locales: string[];
  titles: Names;
  clauses: ClauseView[];
  material: boolean;
  changeNote: string | null;
  approvals: ApprovalView[];
  requestedEffectiveFrom: string | null;
  effectiveFrom: string | null;
  submittedAt: string | null;
  approvedAt: string | null;
  supersededAt: string | null;
  updatedAt: string;
};

export type PolicyStatus = "draft" | "in_review" | "approved" | "published" | "retired";

export type PolicySummary = { id: string; code: string; names: Names; categoryId: string | null; status: PolicyStatus; classification: string; owner: PolicyPerson };

export type PolicyReading = { policy: PolicySummary; version: VersionView; superseded: boolean; currentVersion: number | null; bindingLocale: string | null };

export type RouteStep = { names?: Names | null; approvers?: string[] | null; complianceOfficers?: boolean };

export type PolicyRights = { manage: boolean; edit: boolean; review: boolean; history: boolean };

export type PolicyView = {
  id: string;
  code: string;
  names: Names;
  categoryId: string | null;
  owner: PolicyPerson;
  orgUnitId: string | null;
  audience: ContentAudience;
  classification: string;
  bindingLocale: string | null;
  route: RouteStep[];
  reviewMonths: number;
  related: string[];
  status: PolicyStatus;
  currentVersion: number | null;
  effectiveFrom: string | null;
  nextReviewAt: string | null;
  working: { id: string; state: string; requestedEffectiveFrom: string | null; submittedAt: string | null; updatedAt: string } | null;
  scheduled: { id: string; number: number; effectiveFrom: string } | null;
  rights: PolicyRights;
  createdAt: string;
  updatedAt: string;
};

export type PolicyListItem = {
  id: string;
  code: string;
  names: Names;
  categoryId: string | null;
  owner: PolicyPerson;
  orgUnitId: string | null;
  classification: string;
  status: PolicyStatus;
  currentVersion: number | null;
  effectiveFrom: string | null;
  nextReviewAt: string | null;
  workingState: string | null;
};

export type CategoryView = { id: string; names: Names };

export type VersionHistoryItem = {
  id: string;
  number: number;
  state: string;
  material: boolean;
  changeNote: string | null;
  effectiveFrom: string | null;
  supersededAt: string | null;
  approvals: ApprovalView[];
};

export type AttestationDue = { campaignId: string; version: number; dueAt: string; overdue: boolean; text: Names };

export type MyAttestation = { due: AttestationDue | null; lastVersion: number | null; lastAt: string | null };

export type MyPolicyItem = {
  policy: PolicySummary;
  currentVersion: number | null;
  effectiveFrom: string | null;
  due: AttestationDue | null;
  attestedVersion: number | null;
  attestedAt: string | null;
};

export type AttestationView = { id: string; policyId: string; version: number; campaignId: string | null; locale: string; attestationText: string; textHash: string; channel: string; attestedAt: string };

export const policyHref = (id: string) => `/policies/${encodeURIComponent(id)}`;
export const versionHref = (id: string, number: number) => `/policies/${encodeURIComponent(id)}/versions/${number}`;

/** A name in the reader's language, else English, else the first one given. */
export function nameIn(names: Names | null | undefined, locale: string): string {
  if (!names) return "";
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
}

/** The language a version is shown in: the reader's when the version has it, else the binding one, else its first. */
export function shownLocale(version: VersionView, locale: string, binding: string | null): string {
  if (version.locales.includes(locale)) return locale;
  if (binding && version.locales.includes(binding)) return binding;
  return version.locales[0] ?? locale;
}
