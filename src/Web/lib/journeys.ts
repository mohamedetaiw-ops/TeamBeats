/** Journeys (spec 022 R2 slice 3). The API decides every rule: who may see a journey, who does each step and when it opens. */
export type Names = Record<string, string>;

export const JOURNEY_KINDS = ["onboarding", "offboarding", "transfer", "promotion"] as const;
export type JourneyKind = (typeof JOURNEY_KINDS)[number];

export const JOURNEY_STARTS = ["hire", "account", "termination", "service", "manual"] as const;
export type JourneyStart = (typeof JOURNEY_STARTS)[number];

export const JOURNEY_ASSIGNEES = ["person", "buddy", "manager", "users", "permission"] as const;
export type JourneyAssignee = (typeof JOURNEY_ASSIGNEES)[number];

export const JOURNEY_STATUSES = ["active", "completed", "cancelled"] as const;
export type JourneyStatus = (typeof JOURNEY_STATUSES)[number];

export const STEP_STATUSES = ["scheduled", "open", "done", "cancelled"] as const;
export type StepStatus = (typeof STEP_STATUSES)[number];

export type JourneyStepSpec = {
  key: string;
  names: Names;
  offsetDays: number;
  assignee: JourneyAssignee;
  users?: string[] | null;
  permission?: string | null;
  dueWorkingDays?: number | null;
};

export type JourneyTemplate = {
  id: string;
  key: string;
  kind: JourneyKind;
  names: Names;
  orgUnitId: string | null;
  startOn: JourneyStart;
  serviceId: string | null;
  anchorField: string | null;
  steps: JourneyStepSpec[];
  active: boolean;
  version: number;
  updatedAt: string;
  eTag: string;
};

export type PersonRef = { id: string; displayName: string };

export type JourneySummary = {
  id: string;
  templateKey: string;
  templateNames: Names;
  kind: JourneyKind;
  person: PersonRef;
  buddy: PersonRef | null;
  orgUnitId: string | null;
  anchorDate: string;
  status: JourneyStatus;
  steps: number;
  done: number;
  open: number;
  overdue: number;
  createdAt: string;
  completedAt: string | null;
};

export type JourneyStep = {
  key: string;
  position: number;
  names: Names;
  offsetDays: number;
  assignee: JourneyAssignee;
  scheduledOn: string;
  status: StepStatus;
  outcome: "done" | "skipped" | null;
  completedBy: PersonRef | null;
  completedAt: string | null;
  dueAt: string | null;
  overdue: boolean;
  myTaskId: string | null;
};

export type Journey = {
  journey: JourneySummary;
  startedBy: string;
  cancelReason: string | null;
  previousJourneyId: string | null;
  steps: JourneyStep[];
  canManage: boolean;
};
