/** Shapes of the agent API (specs 020 and 027). The API decides every rule; these types only carry what it returns. */

export type Localized = Record<string, string>;

export type AgentAudience = { scope: "tenant" | "org_units" | null; orgUnits: string[] | null };

export type AgentDefinition = {
  name: Localized | null;
  description: Localized | null;
  icon: string | null;
  category: string | null;
  instructions: Localized | null;
  tone: string | null;
  outputFormat: string | null;
  knowledge: string[] | null;
  tools: string[] | null;
  audience: AgentAudience | null;
  languages: string[] | null;
  escalation: Localized | null;
  autonomy: string | null;
  maxClassification: string | null;
  maxToolCalls: number | null;
  purpose?: Localized | null;
};

export type AgentStatus = "draft" | "in_review" | "published" | "suspended" | "retired";

export type AgentSummary = {
  id: string;
  name: Localized | null;
  description: Localized | null;
  icon: string | null;
  category: string | null;
  status: AgentStatus;
  riskClass: "low" | "medium" | "high";
  orgUnitId: string;
  ownerUserId: string;
  templateId: string | null;
  templateVersion: number | null;
  currentVersion: number | null;
  latestVersion: number;
  updatedAt: string;
  backupOwnerUserId: string | null;
  ownerFlaggedAt: string | null;
};

export type AgentVersion = {
  number: number;
  state: "draft" | "in_review" | "published" | "rejected";
  riskClass: string;
  revision: number;
  lastEditor: string;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
};

export type AgentDetail = { agent: AgentSummary; definition: AgentDefinition; latest: AgentVersion; versions: AgentVersion[]; canEdit: boolean };

export type TemplateView = {
  id: string;
  version: number;
  name: Localized | null;
  description: Localized | null;
  icon: string | null;
  category: string | null;
  autonomy: string | null;
  tools: string[] | null;
  ready: boolean;
  missingTools: string[];
  blockedTools: string[];
  autonomyAbovePolicy: boolean;
};

export type ToolView = { name: string; module: string; sideEffect: string; defaultRule: string; rule: string; allowed: boolean };

export type FieldChange = { field: string; from: unknown; to: unknown };

export type AgentReview = {
  agent: AgentSummary;
  version: AgentVersion;
  definition: AgentDefinition;
  publishedVersion: number | null;
  changes: FieldChange[];
  gate: AgentGate;
  canApprove: boolean;
  cannotApproveReason: string | null;
};

/** The release gate of a version (spec 034 US5): status, failures by severity, failing cases and the score diff. */
export type AgentGate = {
  status: string;
  blocking: boolean;
  failingCases: string[];
  runId?: string | null;
  canOverride?: boolean;
  criticalFailures?: string[] | null;
  blockingFailures?: string[] | null;
  incomplete?: string[] | null;
  diff?: { metric: string; locale: string; published: number | null; value: number | null }[] | null;
  overrideReason?: string | null;
};

export type AgentPolicy = {
  maxAutonomy: string;
  maxClassification: string;
  allowedTools: string[] | null;
  toolRules: Record<string, string> | null;
  scheduledAgents: boolean;
  persistentMemory: boolean;
  maxToolCalls: number;
  maxSteps: number;
};

export type AgentPolicyView = { version: number; policy: AgentPolicy; updatedAt: string | null };

export type KillSwitchView = { active: boolean; reason: string | null; changedAt: string | null };

export type RunCitation = { n: number; source: string; itemId: string; title: string; link: string };

export type ConfirmationCard = {
  id: string;
  tool: string;
  sideEffect: string;
  parameters: Record<string, unknown>;
  hash: string;
  expiresAt: string;
  fromContent: string[];
};

export type RunView = {
  id: string;
  agentId: string;
  agentVersion: number;
  mode: "run" | "test";
  status: "running" | "waiting" | "completed" | "stopped" | "failed";
  outcome: string | null;
  reason: string | null;
  answer: string | null;
  language: string;
  citations: RunCitation[];
  confirmation: ConfirmationCard | null;
  generated: boolean;
  /** The answer names web addresses; they are shown as text only (spec 027 US5 scenario 3). */
  linksAsText: boolean;
};

export type RunSummary = {
  id: string;
  agentId: string;
  agentVersion: number;
  userId: string;
  mode: string;
  status: string;
  outcome: string | null;
  reason: string | null;
  steps: number;
  toolCalls: number;
  denials: number;
  model: string | null;
  createdAt: string;
  finishedAt: string | null;
};

export type RunStep = {
  n: number;
  kind: "model" | "tool" | "confirmation";
  tool: string | null;
  decision: string;
  reason: string | null;
  parameterHash: string | null;
  parameters: unknown;
  classification: string | null;
  durationMs: number;
  at: string;
};

export type RunRecord = {
  run: RunSummary;
  steps: RunStep[];
  sources: RunCitation[];
  route: { id: string; method: string; candidates: number; agentId: string | null; model: string | null; at: string } | null;
  itemSource: string | null;
  itemId: string | null;
};

export type AgentChoice = { id: string; name: Localized | null; description: Localized | null; icon: string | null; category: string | null };

export const AUTONOMY_BUILT = ["L0", "L1", "L2"] as const;
export const TONES = ["neutral", "friendly", "formal"] as const;
export const OUTPUT_FORMATS = ["text", "bullets", "steps"] as const;
export const CLASSIFICATIONS = ["public", "internal", "confidential", "restricted"] as const;

/** The text for a locale, falling back to English, then to any language the item has. */
export function pickText(value: Localized | null | undefined, locale: string): string {
  if (!value) return "";
  return value[locale] ?? value.en ?? Object.values(value)[0] ?? "";
}
