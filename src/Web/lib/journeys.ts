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

export const JOURNEY_STEP_TYPES = ["task", "attestation", "access_removal", "exit_interview"] as const;
export type JourneyStepType = (typeof JOURNEY_STEP_TYPES)[number];

/** The structured exit reasons (spec 022 FR-018), as the API lists them. */
export const EXIT_REASONS = [
  "compensation",
  "career_growth",
  "management",
  "work_life_balance",
  "workload",
  "culture",
  "role_fit",
  "relocation",
  "family",
  "studies",
  "health",
  "retirement",
  "other",
] as const;
export type ExitReason = (typeof EXIT_REASONS)[number];

export type JourneyStepSpec = {
  key: string;
  names: Names;
  offsetDays: number;
  assignee: JourneyAssignee;
  users?: string[] | null;
  permission?: string | null;
  dueWorkingDays?: number | null;
  type?: JourneyStepType | null;
  systems?: string[] | null;
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

export type AccessItem = {
  id: string;
  name: string;
  kind: "connected" | "listed";
  status: "pending" | "removed" | "not_applicable";
  note: string | null;
  confirmedBy: PersonRef | null;
  confirmedAt: string | null;
  escalatedAt: string | null;
};

export type JourneyStep = {
  key: string;
  position: number;
  names: Names;
  type: JourneyStepType;
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
  accessItems: AccessItem[] | null;
};

/** An exit interview as the viewer may see it: the free text only for HR officers. */
export type ExitInterview = {
  declined: boolean;
  reasons: ExitReason[];
  primaryReason: ExitReason | null;
  rating: number | null;
  wouldReturn: boolean | null;
  comment: string | null;
  submittedBy: PersonRef;
  submittedAt: string;
};

export type ExitReasonCount = { reason: ExitReason; chosen: number; primary: number };

export type ExitReasons = {
  from: string;
  to: string;
  minimumGroupSize: number;
  interviews: number;
  declined: number;
  reasons: ExitReasonCount[];
  units: { orgUnitId: string | null; interviews: number; reasons: ExitReasonCount[] }[];
  hidden: number;
};

export type Journey = {
  journey: JourneySummary;
  startedBy: string;
  cancelReason: string | null;
  previousJourneyId: string | null;
  steps: JourneyStep[];
  canManage: boolean;
  exitInterview: ExitInterview | null;
};
