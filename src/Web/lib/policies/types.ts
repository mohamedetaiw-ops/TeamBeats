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
  /** The controlled document this version is kept as (FR-003), with its reader page. */
  document?: PolicyDocumentSource | null;
};

export type PolicyDocumentSource = { documentId: string; versionId: string; name: string; number: string | null; label: string; fileName: string; link: string };

export type PolicyStatus = "draft" | "in_review" | "approved" | "published" | "retired";

export type PolicySummary = { id: string; code: string; names: Names; categoryId: string | null; status: PolicyStatus; classification: string; owner: PolicyPerson };

export type PolicyReading = { policy: PolicySummary; version: VersionView; superseded: boolean; currentVersion: number | null; bindingLocale: string | null };

export type RouteStep = { names?: Names | null; approvers?: string[] | null; complianceOfficers?: boolean };

export type PolicyRights = { manage: boolean; edit: boolean; review: boolean; history: boolean; compliance: boolean; ask: boolean; analytics: boolean; comprehension?: boolean; exemptions?: boolean; controls?: boolean; paper?: boolean; pause?: boolean; cycle?: boolean; document?: boolean };

export type RegisterRights = { create: boolean; exemptions?: boolean; evidence?: boolean; team?: boolean };

/** One report who still owes an attestation, on the manager's team dashboard (spec 025 R2). */
export type TeamMember = { person: PolicyPerson; direct: boolean; status: MemberStatus; dueAt: string; pausedUntil: string | null; exemptUntil: string | null };

export type TeamPolicyStatus = {
  policy: PolicySummary;
  campaignId: string;
  version: number;
  deadline: string;
  counts: ComplianceCounts;
  paused: number;
  exempt: number;
  owing: TeamMember[];
};

/** A line manager's team across every open campaign (spec 025 R2 manager dashboards). */
export type TeamDashboard = { reports: number; direct: number; policies: TeamPolicyStatus[] };

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
  onboardingRequired: boolean;
  /** The fixed re-attestation cycle in months, for the owner and compliance officers (spec 025 FR-008). */
  attestationCycleMonths?: number | null;
  /** The training course linked to the policy (spec 028 US4), and whether attesting waits for it. */
  training?: { courseId: string; beforeAttestation: boolean } | null;
  /** Days before the review date when the owner gets the review task (spec 025 US5). */
  reviewWindowDays?: number;
  /** Where the policy stands with its review date, for the owner and compliance officers. */
  reviewState?: ReviewState | null;
};

/** Where a policy in force stands with its review date (spec 025 US5); the API works it out. */
export type ReviewState = "open" | "due_soon" | "overdue";

export type ReviewOutcome = "no_change" | "minor_update" | "major_revision" | "retire" | "revised";

export type ReviewView = {
  id: string;
  version: number;
  dueAt: string;
  openedAt: string;
  overdueAt: string | null;
  outcome: ReviewOutcome | null;
  note: string | null;
  reviewer: PolicyPerson | null;
  completedAt: string | null;
  draftVersionId: string | null;
  nextReviewAt: string | null;
};

export type ReviewsView = {
  policy: PolicySummary;
  reviewMonths: number;
  windowDays: number;
  nextReviewAt: string | null;
  state: ReviewState | null;
  open: ReviewView | null;
  history: ReviewView[];
  rights: { complete: boolean; retire: boolean; revise: boolean };
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
  reviewState?: ReviewState | null;
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

/** What the person owes: through a campaign, or without one for an onboarding policy (spec 025 FR-017). */
export type AttestationDue = { campaignId: string | null; version: number; dueAt: string; overdue: boolean; text: Names };

/** What the person owes; trainingFirst names a linked course to complete before attesting (spec 028 US4). */
export type MyAttestation = { due: AttestationDue | null; lastVersion: number | null; lastAt: string | null; trainingFirst?: string | null; check?: CheckDue | null };

/** A campaign's comprehension check as the person has done it (spec 025 US6). */
export type CheckDue = { campaignId: string; passPercent: number; attempts: number; used: number; passed: boolean; needsFollowUp: boolean };

export type CheckOption = { key: string; texts: Names };

export type CheckQuestion = { id: string; position: number; clauseKey: string; clauseNumber: string | null; prompts: Names; options: CheckOption[]; multiple: boolean; correct: string[] | null };

export type CheckQuestions = { versionId: string; number: number | null; state: string; locked: boolean; questions: CheckQuestion[] };

export type RereadClause = { key: string; anchor: string; number: string; titles: Names };

export type CheckResult = { attemptId: string; attempt: number; correct: number; total: number; score: number; passed: boolean; attemptsLeft: number; needsFollowUp: boolean; reread: RereadClause[] };

export type CheckState = { policyId: string; version: number; due: CheckDue; current: { id: string; attempt: number; questions: CheckQuestion[] } | null; last: CheckResult | null };

export const checkHref = (id: string) => `/policies/${encodeURIComponent(id)}/check`;
export const questionsHref = (id: string) => `/policies/${encodeURIComponent(id)}/questions`;

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

export type ClauseChange = { key: string; change: "added" | "removed" | "changed" | "unchanged"; before: ClauseView | null; after: ClauseView | null };

/** The AI change summary of a version in review (spec 025 section 11), always shown labelled as AI-generated. */
export type ChangeSummaryView = {
  outcome: "summarized" | "nothing_to_compare" | "policy_blocked" | "unavailable";
  summary: { locale: string; summary: string; points: string[]; model: string; generatedAt: string; aiGenerated: boolean } | null;
  denial?: string | null;
};

export type CompareView = { base: number | null; versionId: string; number: number | null; clauses: ClauseChange[]; titlesChanged: boolean };

export type CommentView = {
  id: string;
  versionId: string;
  clauseKey: string;
  author: PolicyPerson;
  text: string;
  createdAt: string;
  resolvedAt: string | null;
  resolvedBy: PolicyPerson | null;
};

export type CampaignCounts = { members: number; attested: number; open: number; overdue: number; leftAudience: number; paper?: number };

export type CampaignView = {
  id: string;
  policyId: string;
  version: number;
  deadline: string;
  attestationText: Names;
  reminderDays: number[];
  lateJoinerDays: number;
  excludedMembershipTypes: string[];
  status: "open" | "closed";
  launchedBy: PolicyPerson;
  launchedAt: string;
  closedAt: string | null;
  counts: CampaignCounts;
  check?: { passPercent: number; attempts: number; questions: number } | null;
};

export type ComplianceCampaign = { id: string; version: number; deadline: string; status: string };

export type ComplianceCounts = CampaignCounts & { percent: number };

export type ComplianceGroup = {
  unitId: string | null;
  unitNames: Names | null;
  direct: boolean;
  location: string | null;
  manager: PolicyPerson | null;
  counts: ComplianceCounts;
};

export type ComplianceView = {
  policyId: string;
  currentVersion: number | null;
  campaign: ComplianceCampaign | null;
  campaigns: ComplianceCampaign[];
  scope: "tenant" | "units" | "reports";
  unit: string | null;
  unitPath: string[];
  totals: ComplianceCounts;
  byUnit: ComplianceGroup[];
  byLocation: ComplianceGroup[];
  byManager: ComplianceGroup[];
};

export type MemberStatus = "open" | "attested" | "overdue" | "left_audience";

export type CompliancePerson = {
  person: PolicyPerson;
  unitId: string | null;
  location: string | null;
  managerId: string | null;
  status: MemberStatus;
  dueAt: string;
  attestedAt: string | null;
  leftAt: string | null;
  /** Failed every attempt of the campaign's comprehension check (spec 025 US6). */
  needsFollowUp?: boolean;
  /** The last day of an approved exemption that covers the person (spec 025 US7). */
  exemptUntil?: string | null;
  /** The day a long leave pause ends, while it lasts (spec 025 edge cases); the reason stays with compliance. */
  pausedUntil?: string | null;
};

export type CompliancePeople = { items: CompliancePerson[]; total: number; offset: number; limit: number };

export type PersonCompliance = { person: PolicyPerson; policies: MyPolicyItem[]; exemptions?: ExemptionView[] | null };

export type ExemptionStatus = "pending" | "approved" | "rejected" | "withdrawn" | "expired";

export type ExemptionView = {
  id: string;
  policy: PolicySummary;
  version: number | null;
  scope: "person" | "unit";
  person: PolicyPerson | null;
  unitId: string | null;
  unitNames: Names | null;
  reason: string;
  endsOn: string;
  endsAt: string;
  status: ExemptionStatus;
  requestedBy: PolicyPerson;
  requestedAt: string;
  approvals: { step: string; approver: PolicyPerson; onBehalfOf: PolicyPerson | null; at: string }[];
  decidedBy: PolicyPerson | null;
  decidedAt: string | null;
  closedAt: string | null;
  canWithdraw: boolean;
};

export type PolicyExemptionsView = {
  policy: PolicySummary;
  items: ExemptionView[];
  rights: { request: boolean; requestFor: boolean; seesAll: boolean };
};

export const exemptionsHref = (id: string) => `/policies/${encodeURIComponent(id)}/exemptions`;
export const exemptionRegisterHref = "/policies/exemptions";

export type EvidencePackStatus = "queued" | "ready" | "failed";

/** An evidence pack for auditors (spec 025 US8): what it covers, whether its files are ready, and where it is shared. */
export type EvidencePackView = {
  id: string;
  status: EvidencePackStatus;
  policies: { id: string; code: string; names: Names }[];
  from: string;
  to: string;
  locale: string;
  requestedBy: PolicyPerson;
  requestedAt: string;
  completedAt: string | null;
  pdf: boolean;
  csv: boolean;
  sharedSpace: { id: string; name: string } | null;
  sharedUntil: string | null;
  canShare: boolean;
};

export const evidenceHref = "/policies/evidence";
export const evidenceApi = "/api/v1/policies/evidence-packs";

export const editHref = (id: string) => `/policies/${encodeURIComponent(id)}/edit`;
export const reviewHref = (id: string) => `/policies/${encodeURIComponent(id)}/review`;
export const reviewsHref = (id: string) => `/policies/${encodeURIComponent(id)}/reviews`;
export const campaignsHref = (id: string) => `/policies/${encodeURIComponent(id)}/campaigns`;
export const complianceHref = (id: string) => `/policies/${encodeURIComponent(id)}/compliance`;
export const personComplianceHref = (userId: string) => `/policies/compliance/people/${encodeURIComponent(userId)}`;

export type MappedControl = {
  framework: string;
  frameworkVersion: string | null;
  frameworkNames: Names | null;
  code: string;
  titles: Names | null;
  known: boolean;
};

export type PolicyControlsView = { policy: PolicySummary; controls: MappedControl[]; canEdit: boolean };

export type LibraryFramework = { key: string; version: string; names: Names; controls: { code: string; titles: Names }[] };

export type ControlPoliciesView = {
  control: MappedControl;
  policies: { policy: PolicySummary; currentVersion: number | null; effectiveFrom: string | null; nextReviewAt: string | null; reviewState: string | null }[];
};

export const controlsHref = (id: string) => `/policies/${encodeURIComponent(id)}/controls`;
export const paperHref = (id: string) => `/policies/${encodeURIComponent(id)}/paper`;

/** A paper attestation import (spec 025 section 14): the scanned forms, and once the CSV ran, what it imported and refused by line. */
export type PaperImportView = {
  id: string;
  policyId: string;
  importedBy: PolicyPerson;
  createdAt: string;
  scanName: string;
  scanType: string;
  scanSize: number;
  status: "draft" | "completed";
  completedAt: string | null;
  imported: number;
  rejected: { line: number; key: string; message: string | null }[];
};
export const controlPoliciesHref = (framework: string, code: string) =>
  `/policies/controls?framework=${encodeURIComponent(framework)}&code=${encodeURIComponent(code)}`;
