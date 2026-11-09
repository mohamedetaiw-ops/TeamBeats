import type { AgentGate, Localized } from "@/lib/agents/types";

/** Shapes of the evaluation API (spec 034). The API decides what the viewer may see and do; pages only show it. */

export type EvalAgentRow = {
  agentId: string;
  name: Localized | null;
  orgUnitId: string;
  status: string;
  currentVersion: number | null;
  latestVersion: number;
  gateStatus: string;
};

export type MetricValue = { metric: string; locale: string; value: number | null; status: string };

export type VersionResult = {
  version: number;
  current: boolean;
  gate: AgentGate;
  runId: string | null;
  verdict: string | null;
  finishedAt: string | null;
  metrics: MetricValue[];
};

export type DashboardView = {
  agentId: string;
  name: Localized | null;
  currentVersion: number | null;
  latestVersion: number;
  versions: VersionResult[];
  canDesign: boolean;
};

export type RunSummary = {
  id: string;
  agentId: string;
  agentVersion: number;
  trigger: string;
  status: string;
  total: number;
  done: number;
  errors: number;
  verdict: string | null;
  requestedBy: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
};

export type SuiteMetric = { key: string; weight: number; threshold: number | null; severity: string | null };

export type EffectiveRule = { key: string; threshold: number | null; severity: string | null; thresholdLevel: string | null; severityLevel: string | null };

export type LocaleScore = { locale: string; cases: number; value: number | null; failures: number; notEvaluated: number; status: string };

export type MetricScore = {
  key: string;
  weight: number;
  threshold: number | null;
  severity: string | null;
  thresholdLevel: string | null;
  locales: LocaleScore[];
  overall: number | null;
};

export type SuiteScore = { key: string; metrics: MetricScore[]; weightedScore: Record<string, number | null> };

export type Regression = { suite: string; metric: string; locale: string; baseline: number; value: number; drop: number; noise: number; cases: number };

export type RunResult = {
  verdict: string;
  suites: SuiteScore[];
  criticalFailures: string[];
  blockingFailures: string[];
  warnings: string[];
  incomplete: string[];
  latencyP95: Record<string, number | null>;
  tokensPerSuccess: Record<string, number | null>;
  baselineRunId: string | null;
  regressions: Regression[];
  metrics: MetricScore[] | null;
};

export type RunSuite = { key: string; kind: string; version: number; metrics: SuiteMetric[]; rules: EffectiveRule[]; datasets: { id: string; version: number }[] };

export type RunDetail = { run: RunSummary; suites: RunSuite[]; result: RunResult | null; canCancel: boolean };

export type ExpectedSource = { source: string | null; itemId: string | null };
export type ExpectedTool = { tool: string | null; parameters: Record<string, unknown> | null };

export type CaseExpectations = {
  answer: string | null;
  keyFacts: string[] | null;
  sources: ExpectedSource[] | null;
  forbiddenSources: ExpectedSource[] | null;
  tools: ExpectedTool[] | null;
  forbiddenTools: string[] | null;
  toolOrder: boolean;
  refusal: boolean;
  escalation: boolean;
};

export type CaseMetric = { state: string; value: number | null; note: string | null };
export type JudgeVerdict = { metric: string; verdict: string; score: number; reason: string; model: string; rubric: string };

export type CaseResult = {
  n: number;
  suite: string;
  datasetId: string | null;
  datasetVersion: number | null;
  caseN: number;
  locale: string;
  input: string;
  expect: CaseExpectations;
  personaId: string;
  severity: string;
  status: string;
  agentRunId: string | null;
  outcome: string | null;
  answer: string | null;
  metrics: Record<string, CaseMetric> | null;
  verdicts: JudgeVerdict[] | null;
  error: string | null;
  model: string | null;
  latencyMs: number | null;
  tokens: number | null;
  failed: boolean;
};

export type CompareMetric = { metric: string; locale: string; a: number | null; b: number | null; delta: number | null };

export type CompareSide = {
  runId: string;
  agentId: string;
  version: number;
  verdict: string | null;
  models: string[];
  tokensPerSuccess: Record<string, number | null>;
  latencyP50: Record<string, number | null>;
  latencyP95: Record<string, number | null>;
};

export type ChangedCase = { suite: string; datasetId: string | null; caseN: number; locale: string; input: string; caseInA: number; caseInB: number };

export type CompareView = { a: CompareSide; b: CompareSide; metrics: CompareMetric[]; nowFailing: ChangedCase[]; nowPassing: ChangedCase[] };

export type MetricDefinition = { key: string; method: string; higherIsBetter: boolean; defaultThreshold: number | null; defaultSeverity: string | null; againstBaseline: boolean };

export type PlatformSuite = { key: string; mandatory: boolean; template: string | null; version: number; names: Localized; metrics: SuiteMetric[]; caseCount: number; locales: string[] };

export type DatasetSummary = { id: string; names: Localized; version: number; caseCount: number; classification: string; locales: string[]; updatedAt: string };

export type AgentSuite = { id: string | null; version: number; metrics: SuiteMetric[]; datasets: string[]; updatedAt: string | null };

export type AgentSuites = {
  agentId: string;
  inherited: PlatformSuite[];
  suite: AgentSuite;
  datasets: DatasetSummary[];
  rules: EffectiveRule[];
  canEdit: boolean;
  weightsAllowed: boolean;
};

export type CaseInput = {
  input: string;
  locale: string;
  personaId: string | null;
  expect: CaseExpectations;
  tags: string[];
  severity: string;
  classification: string;
};

export type CaseView = CaseInput & { n: number };

export type DatasetVersion = { number: number; caseCount: number; classification: string; locales: string[]; source: string; createdBy: string; createdAt: string };

export type DatasetView = { id: string; agentId: string; names: Localized; version: number; versions: DatasetVersion[]; cases: CaseView[]; canEdit: boolean };

export type ImportPreview = { importId: string; baseVersion: number; valid: number; errors: { row: number; field: string; key: string; message: string | null }[]; sample: CaseInput[] };

export type Persona = { id: string; names: Localized; orgUnitId: string; roles: string[]; membershipType: string; createdAt: string };

export type SettingsView = { version: number; thresholds: SuiteMetric[]; platform: EffectiveRule[]; updatedAt: string | null; canEdit: boolean };

export type LocaleAgreement = { locale: string; labels: number; judged: number; agreed: number; value: number | null; status: string };

export type CalibrationView = {
  id: string;
  metric: string;
  rubricVersion: string;
  status: string;
  outcome: string | null;
  agreement: LocaleAgreement[];
  requestedBy: string;
  createdAt: string;
  finishedAt: string | null;
};

export type RubricState = {
  metric: string;
  rubricVersion: string;
  state: string;
  labels: Record<string, number>;
  latest: CalibrationView | null;
  running: boolean;
  canLabel: boolean;
  canCalibrate: boolean;
};

export type CalibrationLabel = {
  id: string;
  metric: string;
  locale: string;
  input: string;
  expect: CaseExpectations;
  answer: string;
  label: string;
  labelledBy: string;
  labelledAt: string;
};

/** Every metric value is a share of cases, or for latency and cost the increase over the baseline, so it reads as a percentage. */
export const LOCALES = ["en", "ar"] as const;
export const SEVERITIES = ["warning", "blocking", "critical"] as const;
export const CLASSIFICATIONS = ["public", "internal", "confidential", "restricted"] as const;
