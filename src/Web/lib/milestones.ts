import type { PersonRef } from "@/lib/leave";

/** Probation and contract milestones (spec 022 US9, FR-027, FR-028): the shapes the API returns. Rules live in the API. */
export type MilestoneKind = "probation" | "contract";
export type MilestoneStatus = "open" | "reviewed" | "closed";
export type MilestoneOutcome = "confirm" | "extend" | "end";
export type MilestoneWriteBack = "hr" | "recorded" | "failed" | "none";

export const MILESTONE_KINDS: readonly MilestoneKind[] = ["probation", "contract"];
export const MILESTONE_OUTCOMES: readonly MilestoneOutcome[] = ["confirm", "extend", "end"];

export type MilestoneReview = {
  id: string;
  dueDate: string;
  outcome: MilestoneOutcome;
  newDate: string | null;
  comment: string;
  reviewedBy: PersonRef;
  reviewedAt: string;
  writeBack: MilestoneWriteBack;
};

export type Milestone = {
  id: string;
  person: PersonRef;
  kind: MilestoneKind;
  dueDate: string;
  source: "hr" | "manual";
  status: MilestoneStatus;
  daysLeft: number;
  reviewOpen: boolean;
  hireDate: string | null;
  jobTitle: string | null;
  reviews: MilestoneReview[];
  rights: { review: boolean; setDate: boolean };
};

export const milestoneTone: Record<MilestoneStatus, "neutral" | "info" | "success" | "warning"> = {
  open: "info",
  reviewed: "warning",
  closed: "success",
};
