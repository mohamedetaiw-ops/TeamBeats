/** Shapes of the compliance projects API (spec 026 US7). The API decides who may read or change each of them. */

export const complianceApi = "/api/v1/compliance";
export const complianceHref = "/compliance";
export const libraryHref = "/compliance/library";
export const projectHref = (id: string) => `/compliance/${id}`;
export const controlHref = (id: string) => `/compliance/controls/${id}`;
export const evidenceFileHref = (id: string) => `${complianceApi}/evidence/${id}/file`;

export const controlStatuses = ["not_started", "in_progress", "evidence_ready", "reviewed"] as const;
export type ControlStatus = (typeof controlStatuses)[number];

export type Names = Record<string, string>;
export type PersonRef = { userId: string; displayName: string };

export type ComplianceRights = { enabled: boolean; canView: boolean; canManage: boolean };

export type FrameworkSummary = { key: string; version: string; names: Names; controlCount: number; platform: boolean; id: string | null; revision: number };
export type LibraryView = { frameworks: FrameworkSummary[]; canManage: boolean };
export type Framework = { key: string; version: string; names: Names; controls: { code: string; titles: Names }[]; platform: boolean; id: string | null; revision: number };

export type ProjectSummary = {
  id: string;
  name: string;
  frameworkKey: string;
  frameworkVersion: string;
  frameworkNames: Names;
  orgUnitId: string | null;
  orgUnitNames: Names | null;
  sharedSpace: { spaceId: string; name: string } | null;
  status: "active" | "archived";
  byStatus: Record<ControlStatus, number>;
  overdueTasks: number;
  rights: { canManage: boolean; readOnly: boolean };
  createdAt: string;
};

export type ControlSummary = {
  id: string;
  code: string;
  titles: Names;
  owner: PersonRef | null;
  status: ControlStatus;
  dueOn: string | null;
  overdue: boolean;
  tasks: number;
  openTasks: number;
  evidence: number;
  policies: number;
  documents: number;
  version: number;
};

export type ProjectView = { project: ProjectSummary; controls: ControlSummary[]; version: number };

export type LinkView = { id: string; code: string | null; names: Names | null; available: boolean };
export type EvidenceView = { id: string; kind: "audit_export"; sourceId: string; format: string; rowCount: number | null; bytes: number; sha256: string; attachedBy: PersonRef | null; attachedAt: string };
export type TaskView = { id: string; title: string; owner: PersonRef | null; dueOn: string | null; doneAt: string | null; overdue: boolean; evidence: EvidenceView[]; canEdit: boolean; canAttach: boolean };
export type ControlDetail = {
  control: ControlSummary;
  projectId: string;
  projectName: string;
  note: string | null;
  policies: LinkView[];
  documents: LinkView[];
  tasks: TaskView[];
  rights: { canEdit: boolean; canChangeStatus: boolean; canReview: boolean; canAddTasks: boolean };
};

export type ReadinessView = {
  projectId: string;
  total: number;
  byStatus: Record<ControlStatus, number>;
  overdueControls: number;
  overdueTasks: { taskId: string; controlId: string; controlCode: string; title: string; owner: PersonRef | null; dueOn: string }[];
  byOwner: { owner: PersonRef | null; byStatus: Record<ControlStatus, number>; overdueTasks: number }[];
  byOrgUnit: { orgUnitId: string | null; names: Names | null; byStatus: Record<ControlStatus, number> }[];
};

export type AuditExportSummary = { id: string; format: string; rowCount: number | null; completedAt: string; expiresAt: string };

/** The text for a locale, falling back to English and then to any text there is. */
export function pick(names: Names | null | undefined, locale: string): string {
  if (!names) return "";
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
}

export const statusTone: Record<ControlStatus, "neutral" | "info" | "accent" | "success"> = {
  not_started: "neutral",
  in_progress: "info",
  evidence_ready: "accent",
  reviewed: "success",
};
