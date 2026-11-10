/** Shapes of the connector pack API (spec 017). The API decides every rule; these types only describe its answers. */

export type PackAction = { name: string; sideEffect: string; idempotent: boolean; businessErrors: string[] };

export type Pack = { kind: string; version: number; inPackage: boolean; actions: PackAction[] };

export type PackCheck = { name: string; ok: boolean; resultKey: string; errorClass?: string | null };

export type LastTest = { at: string; ok: boolean; checks: PackCheck[] };

export type RestAuth = {
  type: string;
  header?: string | null;
  username?: string | null;
  secretRef?: string | null;
  tokenUrl?: string | null;
  clientId?: string | null;
  scope?: string | null;
  expiresOn?: string | null;
};

export type RestSettings = { baseUrl: string; auth: RestAuth };

export type PackConnection = {
  id: string;
  kind: string;
  displayName: string;
  status: string;
  enabled: boolean;
  ownerUserId: string | null;
  lastSuccessAt: string | null;
  lastCheckedAt: string | null;
  credentialExpiresOn: string | null;
  circuitOpen: boolean;
  rateLimitPerMinute: number;
  settings: Partial<RestSettings>;
  lastTest: LastTest | null;
  actions: PackAction[];
};

export type PackTestResult = { status: "ok" | "failed"; checks: PackCheck[] };

export type RestInput = { name: string; type: string; required: boolean };

export type RestBusinessError = { status: number; code: string };

export type RestActionDefinition = {
  method: string;
  path: string;
  query: Record<string, string> | null;
  headers: Record<string, string> | null;
  body: unknown;
  inputs: RestInput[] | null;
  output: Record<string, string> | null;
  referencePath: string | null;
  sideEffect: string;
  idempotencyHeader: string | null;
  businessErrors: RestBusinessError[] | null;
};

export type RestActionView = { name: string; definition: RestActionDefinition; updatedAt: string };

export type RestActionTestResult = {
  outcome: string;
  httpStatus: number | null;
  output: Record<string, unknown> | null;
  externalReference: string | null;
  errorClass: string | null;
  errorCode: string | null;
};

export type RunView = {
  id: string;
  action: string;
  mode: string;
  status: string;
  attempts: number;
  errorClass: string | null;
  errorCode: string | null;
  previousErrorClass: string | null;
  externalReference: string | null;
  recordCount: number | null;
  durationMs: number | null;
  correlationId: string | null;
  replays: number;
  lastReplayedAt: string | null;
  replayable: boolean;
  createdAt: string;
  finishedAt: string | null;
};

export type RunPage = { items: RunView[]; nextCursor: string | null };

export type ReplayResult = { replayed: string[]; skipped: string[] };

export type AllowlistEntry = { host: string; addedAt: string };

export const AUTH_TYPES = ["none", "api_key", "bearer", "basic", "oauth_client_credentials"] as const;
export const METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE"] as const;
export const SIDE_EFFECTS = ["read", "write", "delete"] as const;
export const INPUT_TYPES = ["string", "number", "integer", "boolean"] as const;
export const CONNECTION_STATUSES = ["healthy", "degraded", "needs_consent", "credential_expiring", "disabled"] as const;
export const RUN_STATUSES = ["queued", "running", "retrying", "succeeded", "business_error", "failed", "unknown", "refused"] as const;
export const RUN_FILTERS = ["all", "dead_letter", ...RUN_STATUSES] as const;

/** Result keys of connection checks that have a message on these pages; others show the key's last part. */
export const CHECK_RESULTS = [
  "host_allowed",
  "host_not_allowed",
  "credential_found",
  "credential_missing",
  "token_ok",
  "token_refused",
  "not_responding",
  "settings_invalid",
] as const;

/** Error classes the runtime reports (spec 017 section 6), each with a message on these pages. */
export const ERROR_CLASSES = [
  "business_error",
  "credential_refused",
  "not_responding",
  "throttled",
  "server_error",
  "invalid_response",
  "blocked_address",
  "host_not_allowed",
  "circuit_open",
  "rate_limited",
  "connection_disabled",
  "connection_missing",
  "not_in_package",
  "unknown_action",
  "outcome_unknown",
  "unexpected",
  "invalid_input",
  "credential_missing",
  "request_refused",
] as const;

export function known<T extends string>(list: readonly T[], value: string | null | undefined): value is T {
  return value != null && (list as readonly string[]).includes(value);
}

/** "integrations.test.host_allowed" becomes "host_allowed". */
export function checkResult(resultKey: string): string {
  return resultKey.slice(resultKey.lastIndexOf(".") + 1);
}

/** One "Name: value" pair per line, as typed. Blank lines are skipped; the API validates names and values. */
export function parsePairs(text: string): Record<string, string> {
  const pairs: Record<string, string> = {};
  for (const line of text.split("\n")) {
    const at = line.indexOf(":");
    if (at <= 0) continue;
    const name = line.slice(0, at).trim();
    if (name) pairs[name] = line.slice(at + 1).trim();
  }
  return pairs;
}

export function formatPairs(pairs: Record<string, string> | null | undefined): string {
  return Object.entries(pairs ?? {})
    .map(([k, v]) => `${k}: ${v}`)
    .join("\n");
}

/** Microsoft 365 connections (spec 006 US5, spec 018): features with their Microsoft permissions and consent. */
export type GraphPermissionView = { name: string; type: "Application" | "Delegated" | string; reasonKey: string };

export type FeatureView = {
  key: string;
  enabled: boolean;
  inPackage: boolean;
  consentStatus: "pending" | "granted" | string;
  permissions: GraphPermissionView[];
  grantedPermissions: string[];
  lastTest: unknown;
  options?: { previewLine?: boolean; maxMessages?: number; maxDays?: number } | null;
};

export type Microsoft365Connection = {
  id: string;
  kind: string;
  externalTenantId: string;
  displayName: string;
  status: string;
  enabled: boolean;
  ownerUserId: string | null;
  lastSuccessAt: string | null;
  features: FeatureView[];
};

export type FeatureTestResult = {
  feature: string;
  ok: boolean;
  resultKey: string;
  users: number | null;
  groups: number | null;
  missing: string[];
  broader: string[];
};

export type Microsoft365TestResult = { status: string; features: FeatureTestResult[] };

export const M365_FEATURES = ["directory_sync", "mail", "calendar", "free_busy"] as const;
export const M365_TEST_RESULTS = ["ok", "ok_broader", "consent_missing", "unreachable", "application_mail_permissions"] as const;
export const M365_PERMISSION_REASONS = [
  "user_read_all",
  "group_read_all",
  "administrative_unit_read_all",
  "user_read",
  "mail_read_basic",
  "mail_read",
  "calendars_read_basic",
] as const;

/** Webhook connections (spec 017 US5): settings, the events they may send, and inbound counts. */
export type WebhookAuth = { type: "hmac_sha256"; secretRef: string; overlapHours?: number | null; previousSecretRef?: string | null; previousValidUntil?: string | null };

export type WebhookEventChoice = { type: string; fields: string[] };

export type WebhookInbound = { target: "event" | "message" | string; name: string; correlationPath?: string | null; fields: Record<string, string>; windowSeconds: number };

export type WebhookSettings = { url?: string | null; auth: WebhookAuth; events?: WebhookEventChoice[]; inbound?: WebhookInbound | null };

export type InboundCounts = {
  path: string;
  accepted: number;
  refusedSignature: number;
  refusedWindow: number;
  refusedDuplicate: number;
  lastReceivedAt: string | null;
};

export const WEBHOOK_EVENTS = ["ContentPublished", "ProcessInstanceCompleted", "RequestCompleted", "RequestReturned", "RequestSubmitted", "UserTaskCompleted", "UserTaskCreated"] as const;
export const WEBHOOK_TARGETS = ["event", "message"] as const;
