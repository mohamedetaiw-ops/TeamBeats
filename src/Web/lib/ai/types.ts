/** Shapes of the AI gateway admin API (spec 013). The API authorizes and validates everything; pages only show it. */

export const USE_CASES = ["assistant", "summarize", "translate", "draft", "embed", "agent", "evaluation", "transcribe", "speak"] as const;

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
  sovereign?: string[] | null;
  displayCurrency?: string | null;
  displayRate?: number | null;
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
  gateStatus: string | null;
  canOverride: boolean;
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

export const USAGE_GROUPS = ["day", "use_case", "model", "provider", "classification", "unit", "agent", "user"] as const;

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
  currency: string;
  rate: number;
};

export type BudgetForecast = { id: string; scope: string; scopeId: string | null; amount: number; used: number; projected: number; overBudget: boolean };

export type UsageForecast = {
  monthStart: string;
  monthEnd: string;
  daysElapsed: number;
  daysInMonth: number;
  monthToDate: number;
  dailyRate: number;
  projected: number;
  byProvider: UsageRow[];
  byClassification: UsageRow[];
  budgets: BudgetForecast[];
  scope: "tenant" | "units";
  currency: string;
  rate: number;
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

/** Spec 013 US5: an AI budget and what was used in the current period (null for a per person budget). */
export const BUDGET_SCOPES = ["tenant", "org_unit", "agent", "user", "each_user"] as const;
export const BUDGET_ACTIONS = ["block", "downgrade", "alert"] as const;

export type Budget = {
  id: string;
  scope: (typeof BUDGET_SCOPES)[number];
  scopeId: string | null;
  period: "month" | "day";
  amount: number | null;
  requests: number | null;
  action: (typeof BUDGET_ACTIONS)[number];
  downgradeModel: string | null;
  ownerId: string | null;
  version: number;
  used: number | null;
  periodStart: string;
  resetsAt: string;
  updatedAt: string;
};

export type BudgetsView = {
  editable: boolean;
  scopedAllowed: boolean;
  readOnlyReason: string | null;
  budgets: Budget[];
  labels: Record<string, Record<string, string>>;
};

/** Spec 013 US7: what an org unit narrows, and what it inherits from the tenant policy and the units above it. */
export const CLASSIFICATIONS = ["public", "internal", "confidential", "restricted"] as const;

export type OrgPolicyDocument = {
  allowlist: string[] | null;
  confidential: string[] | null;
  maxClassification: string | null;
  maxOutputTokens: number | null;
  minTemperature: number | null;
  maxTemperature: number | null;
};

export type UnitLimits = {
  allowlist: string[];
  confidential: string[];
  maxClassification: string;
  maxOutputTokens: number | null;
  minTemperature: number | null;
  maxTemperature: number | null;
};

export type OrgPolicyView = {
  unitId: string;
  version: number;
  policy: OrgPolicyDocument | null;
  parent: UnitLimits;
  source: Record<string, string>;
  editable: boolean;
};

export type OrgPolicySummary = { unitId: string; version: number; policy: OrgPolicyDocument; updatedAt: string };

export const MODEL_CAPABILITIES = ["chat", "tools", "structured_output", "vision", "embeddings", "transcription", "speech"] as const;

export type PrivateModel = { name: string; capabilities: string[]; contextTokens: number };

export type EndpointCheck = { check: string; model: string | null; passed: boolean; detail: string | null };

export type PrivateEndpoint = {
  id: string;
  name: string;
  baseUrl: string;
  secretRef: string;
  models: PrivateModel[];
  region: string;
  maxClassification: string;
  status: "enabled" | "disabled";
  version: number;
  lastTestAt: string | null;
  lastTestPassed: boolean | null;
  checks: EndpointCheck[];
  testCurrent: boolean;
  catalogueModels: string[];
  updatedAt: string;
};

export type PrivateEndpointsView = { editable: boolean; readOnlyReason: string | null; endpoints: PrivateEndpoint[] };
