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
