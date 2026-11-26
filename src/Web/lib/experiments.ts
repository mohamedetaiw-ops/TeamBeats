/** Governed experience experiments (spec 021 R3, FR-022): /api/v1/experience/experiments and /api/v1/analytics/experiments/{id}. */
export type ExperimentStatus = "proposed" | "running" | "stopped";

export type Experiment = {
  id: string;
  pageId: string;
  slug: string;
  name: string;
  controlRelease: number;
  variantRelease: number;
  percent: number;
  orgUnitId: string | null;
  status: ExperimentStatus;
  proposedBy: string;
  proposedAt: string;
  approvedBy: string | null;
  startedAt: string | null;
  stoppedAt: string | null;
};

/** One arm's results; the counts and rate are null under the minimum group size. */
export type ExperimentArmResult = { arm: "control" | "variant"; people: number; completedTask: number | null; openedItem: number | null; completionRate: number | null };

export type ExperimentResults = { experimentId: string; minGroupSize: number; arms: ExperimentArmResult[] };

export type ExperimentChoice = { available: boolean; consented: boolean };

/** A page that can be tried: published, with at least one earlier release. */
export type ExperimentPage = { id: string; slug: string; currentRelease: number | null };
