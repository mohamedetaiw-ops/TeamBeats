/** Process models, decisions, operations and monitoring of spec 024 R2. The API decides every rule; these are its shapes. */
export type Names = Record<string, string>;

export type ModelProblem = { elementId: string; key: string; message: string };

export type ProcessModelSummary = {
  key: string;
  ownerOrgUnitId: string | null;
  names: Names;
  problems: number;
  latestVersion: number | null;
  updatedAt: string;
};

export type ProcessVersion = { version: number; publishedAt: string; publishedBy: string | null; activeInstances: number };

export type ProcessModelView = {
  key: string;
  ownerOrgUnitId: string | null;
  names: Names;
  bpmnXml: string;
  problems: ModelProblem[];
  updatedAt: string;
  versions: ProcessVersion[];
};

export type DecisionSummary = {
  key: string;
  ownerOrgUnitId: string | null;
  names: Names;
  problems: number;
  latestVersion: number | null;
  updatedAt: string;
};

export type DecisionVersion = { version: number; publishedAt: string; publishedBy: string | null };

export type DecisionModelView = {
  key: string;
  ownerOrgUnitId: string | null;
  names: Names;
  dmnXml: string;
  problems: ModelProblem[];
  updatedAt: string;
  versions: DecisionVersion[];
};

export type DecisionTestResult = { result: unknown; rules: number[]; error: string | null };

export const INSTANCE_STATES = ["active", "completed", "cancelled"] as const;
export type InstanceState = (typeof INSTANCE_STATES)[number];

export type InstanceSummary = {
  id: string;
  process: string;
  version: number;
  businessKey: string;
  orgUnitId: string | null;
  startedBy: string | null;
  status: InstanceState;
  outcome: string | null;
  startedAt: string;
  endedAt: string | null;
  openIncidents: number;
};

export type Incident = {
  id: string;
  instanceId: string;
  elementId: string;
  stepName: string;
  errorClass: string;
  errorCode: string | null;
  status: "open" | "resolved";
  resolution: string | null;
  reason: string | null;
  raisedAt: string;
  resolvedAt: string | null;
};

export type InstanceTask = { id: string; elementId: string; name: string; status: string; dueAt: string | null; createdAt: string };

export type HistoryEntry = { elementId: string; event: string; actorId: string | null; at: string };

export type InstanceDetail = {
  instance: InstanceSummary;
  variables: Record<string, unknown>;
  active: { elementId: string; name: string; status: string }[];
  tasks: InstanceTask[];
  incidents: Incident[];
  history: HistoryEntry[];
};

export const INCIDENT_RESOLUTIONS = ["retried", "skipped", "cancelled"] as const;
export type IncidentResolution = (typeof INCIDENT_RESOLUTIONS)[number];

export type ElementMonitoring = {
  elementId: string;
  name: string;
  active: number;
  incidents: number;
  finished: number;
  averageSeconds: number | null;
  p90Seconds: number | null;
};

export type ProcessMonitoring = {
  process: string;
  version: number;
  from: string;
  to: string;
  active: number;
  completed: number;
  cancelled: number;
  withIncidents: number;
  averageSeconds: number | null;
  p90Seconds: number | null;
  elements: ElementMonitoring[];
  slowest: string[];
};

/** A duration in seconds as hours and minutes, or minutes and seconds when short; the words come from the caller's messages. */
export function durationParts(seconds: number): { unit: "seconds" | "minutes" | "hours" | "days"; value: number } {
  if (seconds < 120) return { unit: "seconds", value: Math.round(seconds) };
  if (seconds < 7200) return { unit: "minutes", value: Math.round(seconds / 60) };
  if (seconds < 172800) return { unit: "hours", value: Math.round(seconds / 3600) };
  return { unit: "days", value: Math.round(seconds / 86400) };
}

export const TASK_OUTCOMES = ["approve", "reject", "return"] as const;

export type TestStep = {
  elementId: string;
  name: string;
  event: string;
  detail: Record<string, unknown> | null;
  assignees: { id: string; name: string }[] | null;
  permission: string | null;
  lineManager: boolean;
};

/** A sandbox run of a draft: nothing is created or sent. Outcome is completed, terminated, error:<code>, incident, waiting, stopped or invalid. */
export type ProcessTestResult = { outcome: string; steps: TestStep[]; variables: Record<string, unknown>; problems: ModelProblem[] };

/** Instance migration of spec 024 R3 (FR-015, US6): a proposed plan and the report of a dry run or a move. */
export type MigrationPlanRow = { elementId: string; name: string; type: string; target: string | null };

export type MigrationPlan = {
  process: string;
  fromVersion: number;
  toVersion: number;
  elements: MigrationPlanRow[];
  targetElements: { elementId: string; name: string; type: string }[];
  activeInstances: number;
};

export type MigrationOutcome = { instanceId: string; businessKey: string | null; migratable: boolean; reason: string | null; elementId: string | null };

export type MigrationReport = { executed: boolean; migratable: number; refused: number; instances: MigrationOutcome[] };

export const MIGRATION_REASONS = [
  "unmapped",
  "kind_changed",
  "structure_changed",
  "wait_changed",
  "join_changed",
  "compensating",
  "compensation_changed",
  "not_active",
  "other_version",
  "not_found",
] as const;

/** Import of a .bpmn file from another tool (spec 024 R3, US7). */
export type ImportItem = { elementId: string; key: string; detail: string | null; message: string };

export type ProcessImportReport = { imported: boolean; model: ProcessModelView | null; converted: ImportItem[]; unsupported: ImportItem[] };

/** A Camunda 8 user task as its assignee sees it (spec 024 US8). The API decides who sees it and what can be done. */
export type ExternalTask = {
  id: string;
  engine: string;
  name: string;
  processName: string | null;
  createdAt: string;
  dueAt: string | null;
  status: "open" | "completing" | "completed" | "failed" | "closed";
  outcome: string | null;
  comment: string | null;
  lastError: string | null;
  attempts: number;
  nextAttemptAt: string | null;
  completedAt: string | null;
};

/** The outcomes the API accepts, sent to Camunda as the variable `outcome`. */
export const EXTERNAL_TASK_OUTCOMES = ["completed", "approved", "rejected"] as const;

/** An agent task of a published version, as its approvers see it (spec 027 FR-024). */
export type AgentTaskView = { elementId: string; names: Record<string, string>; agentId: string; tools: string[]; maxMinutes: number };

/** The dual approval of a version for autonomous agent steps: a process owner and an AI policy administrator, two people. */
export type ProcessAutonomy = {
  key: string;
  version: number;
  tasks: AgentTaskView[];
  ownerBy: string | null;
  ownerAt: string | null;
  policyBy: string | null;
  policyAt: string | null;
  approved: boolean;
  canApproveAs: ("owner" | "policy")[];
};
export const AUTONOMY_ROLES = ["owner", "policy"] as const;
