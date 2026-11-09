/** Shapes of the AI gateway admin API (spec 013). The API authorizes and validates everything; pages only show it. */

export const USE_CASES = ["assistant", "summarize", "translate", "draft", "embed", "agent", "evaluation"] as const;

export type CatalogueEntry = {
  model: string;
  provider: string;
  names: Record<string, string>;
  capabilities: string[];
  contextTokens: number;
  regions: string[];
  zeroRetention: boolean;
  noTraining: boolean;
  maxClassification: string;
  inputPrice: number;
  outputPrice: number;
  status: string;
  deprecatedOn: string | null;
  confidentialEligible: boolean;
};

export type PolicyDocument = {
  allowlist: string[];
  routes: Record<string, string[]>;
  confidential: string[];
  maxOutputTokens: number | null;
  minTemperature: number | null;
  maxTemperature: number | null;
  regions: string[] | null;
  logging: string;
};

export type RouteChange = {
  id: string;
  status: string;
  baseVersion: number;
  useCases: string[];
  before: Record<string, string[]>;
  after: Record<string, string[]>;
  proposedBy: string;
  proposedAt: string;
  decidedAt: string | null;
  overrideReason: string | null;
  failures: string[];
  gateAvailable: boolean;
};

export type ModelPolicyView = {
  version: number;
  source: "tenant" | "platform";
  editable: boolean;
  readOnlyReason: string | null;
  policy: PolicyDocument;
  effectiveRoutes: Record<string, string[]>;
  pending: RouteChange | null;
};

export type PolicySaveResult = { applied: boolean; policy: ModelPolicyView; routeChange: RouteChange | null };

export const USAGE_GROUPS = ["day", "use_case", "model", "provider", "unit", "agent", "user"] as const;

export type UsageRow = { key: string | null; requests: number; inputTokens: number; outputTokens: number; cost: number; users: number };

export type UsageView = {
  from: string;
  to: string;
  groupBy: string;
  scope: "tenant" | "units";
  totals: UsageRow;
  rows: UsageRow[];
  denied: { reason: string; requests: number }[];
  minimumGroupSize: number;
  hiddenGroups: number;
  labels: Record<string, Record<string, string>>;
};

export type DebugWindow = {
  id: string;
  useCase: string;
  openedBy: string;
  openedAt: string;
  endsAt: string;
  closedAt: string | null;
  open: boolean;
  entries: number;
};

export type DebugEntry = { id: string; at: string; useCase: string; classification: string; model: string; text: string | null; correlationId: string | null };

export const DENIALS = [
  "not_in_package",
  "quota_exhausted",
  "budget_exhausted",
  "classification_blocked",
  "model_not_allowed",
  "region_blocked",
  "provider_unavailable",
  "context_too_large",
  "classification_missing",
  "invalid_request",
] as const;

/** A label key for a value the API sends, or null when the value is new to this build (shown as sent). */
export function known<T extends string>(list: readonly T[], value: string): T | null {
  return (list as readonly string[]).includes(value) ? (value as T) : null;
}
