/** Shapes of the connector pack API (spec 017). The API decides every rule; these types only describe its answers. */

export type ConnectorInput = { name: string; type: string; required: boolean };

/** An action of a pack. `inputs` is null when the action declares none; only actions with inputs can be agent tools or route steps. */
export type PackAction = { name: string; sideEffect: string; idempotent: boolean; businessErrors: string[]; inputs?: ConnectorInput[] | null };

/** A connector action registered as an agent tool (spec 017 T009, spec 027 FR-006). */
export type ConnectorTool = {
  id: string;
  connectionId: string;
  action: string;
  name: string;
  sideEffect: string;
  approvalRule: string;
  description: string;
  title: Record<string, string>;
  createdAt: string;
};

export const TOOL_SIDE_EFFECTS = ["read", "write_external"] as const;
export const TOOL_RULES = ["execute", "confirm"] as const;

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

export type RestSettings = { baseUrl: string; auth: RestAuth; serviceDeskId?: string | null; requestTypeId?: string | null };

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
  captured: boolean;
};

/** Payload capture of a connection (spec 017 FR-007): until is null while it is off. */
export type CaptureView = { until: string | null };
export type RunCapture = { runId: string; input: unknown; output: unknown; capturedAt: string; expiresAt: string };
export const CAPTURE_HOURS = [1, 4, 8, 24] as const;

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
  "agent_enrolled",
  "agent_missing",
  "agent_online",
  "agent_offline",
  "system_ok",
  "system_refused",
  "system_failed",
  "clock_upload",
  "clock_host_key",
  "clock_credential",
  "clock_directory",
  "clock_unreachable",
  "clock_files",
  "clock_no_files",
  "google_key_invalid",
  "google_delegation_refused",
  "google_drives_ok",
  "google_drives_missing",
] as const;

/** Connector pack kinds with their own name on these pages. */
export const PACK_KINDS = ["custom_rest", "webhook", "onprem_ad", "servicenow", "jira_service_management", "slack", "clock_import", "finance_rest", "google_workspace", "hr_rest", "hr_sftp", "lms_rest"] as const;

/** Settings of a clock data connection (spec 017 US7). Columns are header names, or numbers from 1 without a header row. */
export type ClockColumns = {
  employeeNumber: string | null;
  punchId: string | null;
  timestamp: string | null;
  date: string | null;
  time: string | null;
  direction: string | null;
  device: string | null;
};
export type ClockSettings = {
  source: "sftp" | "upload";
  sftp: {
    host: string | null;
    port: number | null;
    username: string | null;
    authType: string | null;
    secretRef: string | null;
    hostKeySha256: string | null;
    directory: string | null;
    filePattern: string | null;
  } | null;
  delimiter: string;
  hasHeader: boolean;
  columns: ClockColumns;
  timestampFormat: string | null;
  dateFormat: string | null;
  timeFormat: string | null;
  timeZone: string;
  inValues: string[];
  outValues: string[];
  importAt: string | null;
};
export const CLOCK_SOURCES = ["sftp", "upload"] as const;
export const CLOCK_AUTH = ["password", "private_key"] as const;
export const CLOCK_DELIMITERS = { comma: ",", semicolon: ";", tab: "\t", bar: "|" } as const;
export const CLOCK_COLUMNS = ["employeeNumber", "punchId", "timestamp", "date", "time", "direction", "device"] as const;
export const CLOCK_FILE_STATUSES = ["pending", "imported", "refused"] as const;
export const CLOCK_FILE_ERRORS = ["time_zone", "empty", "missing_column", "too_many_rows", "too_large"] as const;
export const CLOCK_REASONS = ["unknown_employee", "missing_employee", "bad_time", "bad_direction", "too_long"] as const;

export type ClockFile = {
  id: string;
  source: string;
  name: string;
  sizeBytes: number;
  status: string;
  errorKey: string | null;
  rowCount: number;
  imported: number;
  duplicates: number;
  unmatched: number;
  firstAt: string | null;
  lastAt: string | null;
  receivedAt: string;
  importedAt: string | null;
};
export type ClockFilePage = { items: ClockFile[]; nextCursor: string | null };
/** Settings of a Google Workspace connection (spec 017 T019). The key stays in the secret manager. */
export type GoogleSettings = {
  domain: string;
  adminEmail: string;
  serviceAccountEmail: string;
  privateKeyRef: string;
  customerId: string | null;
  features: string[];
  sharedDrives: string[];
  expiresOn: string | null;
};
export const GOOGLE_FEATURES = ["directory", "drive", "calendar"] as const;

/** Settings of an LMS connection beyond address and sign-in (spec 028 US6). */
export type LmsSettings = {
  coursesPath: string | null;
  coursesItemsPath: string | null;
  courseFields: Record<string, string> | null;
  assignPath: string | null;
  assignFields: Record<string, string> | null;
  completionsPath: string | null;
  completionsItemsPath: string | null;
  completionFields: Record<string, string> | null;
  nextPath: string | null;
  idempotencyHeader: string | null;
};
export const LMS_AUTH = ["api_key", "bearer", "basic", "oauth_client_credentials"] as const;
export const LMS_COURSE_FIELDS = ["external_id", "title", "summary", "launch_url", "language"] as const;
export const LMS_ASSIGN_FIELDS = ["reference", "course_id", "learner_email", "due_on"] as const;
export const LMS_COMPLETION_FIELDS = ["course_id", "learner_email", "completed_at", "score"] as const;

/** Settings of a finance hand-off connection beyond address and sign-in (spec 017 FR-021). */
export type FinanceSettings = {
  submitPath: string | null;
  statusPath: string | null;
  fields: Record<string, string> | null;
  linesField: string | null;
  lineFields: Record<string, string> | null;
  referencePath: string | null;
  statusValuePath: string | null;
  statusValues: Record<string, string[]> | null;
  idempotencyHeader: string | null;
};
export const FINANCE_AUTH = ["api_key", "bearer", "basic", "oauth_client_credentials"] as const;
export const FINANCE_FIELDS = ["source_id", "employee_number", "currency", "amount", "claim_date", "description", "cost_centre", "project"] as const;
export const FINANCE_LINE_FIELDS = ["category", "date", "amount", "description", "original_amount", "original_currency"] as const;
export const FINANCE_STATUSES = ["submitted", "approved", "paid", "rejected"] as const;
export const FINANCE_STATES = ["queued", "sent", "failed", "unknown"] as const;
export type FinanceHandOff = {
  id: string;
  sourceType: string;
  sourceId: string;
  state: string;
  externalReference: string | null;
  paymentStatus: string | null;
  errorClass: string | null;
  errorCode: string | null;
  createdAt: string;
  sentAt: string | null;
  checkedAt: string | null;
};

export type ClockProblem = { line: number; employeeNumber: string | null; reason: string };

/** Ticketing packs (spec 017 T017) and the sign-in methods each offers in its form; the API checks them again. */
export const TICKETING_KINDS = {
  servicenow: { auth: ["basic", "oauth_client_credentials"], desk: false },
  jira_service_management: { auth: ["basic", "bearer"], desk: true },
} as const;
export type TicketingKind = keyof typeof TICKETING_KINDS;
export const isTicketing = (kind: string): kind is TicketingKind => Object.prototype.hasOwnProperty.call(TICKETING_KINDS, kind);

/** Settings of an on-premises Active Directory connection (spec 017 US6). The service account stays with the agent. */
export type OnPremSettings = { host: string | null; port: number | null; baseDn: string | null; userFilter: string | null; groupFilter: string | null };

/** A one-time enrolment code, shown once. */
export type EnrolmentCode = { code: string; expiresAt: string };

export type SyncAgent = {
  id: string;
  name: string;
  thumbprint: string;
  enrolledAt: string;
  lastContactAt: string | null;
  version: string | null;
  online: boolean;
  revokedAt: string | null;
};

export type AgentJob = {
  id: string;
  kind: string;
  status: string;
  takenBy: string | null;
  attempts: number;
  errorClass: string | null;
  users: number | null;
  groups: number | null;
  createdAt: string;
  finishedAt: string | null;
};

export const AGENT_JOB_STATUSES = ["queued", "taken", "succeeded", "failed"] as const;

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
  "host_key_mismatch",
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

export const M365_FEATURES = ["directory_sync", "mail", "calendar", "free_busy", "office_editing", "sharepoint_documents", "sharepoint_upload"] as const;
export const M365_TEST_RESULTS = ["ok", "ok_broader", "consent_missing", "unreachable", "application_mail_permissions", "site_missing"] as const;
export const M365_PERMISSION_REASONS = [
  "user_read_all",
  "group_read_all",
  "administrative_unit_read_all",
  "user_read",
  "mail_read_basic",
  "mail_read",
  "calendars_read_basic",
  "files_read_write",
  "sites_selected",
  "files_read_all",
  "files_read_write_all",
] as const;

/** An allowed SharePoint site of the SharePoint documents feature (spec 011 FR-025). */
export type SharePointSite = { siteId: string; name: string; webUrl: string; addedAt: string };

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
export const WEBHOOK_TARGETS = ["event", "message", "kpi"] as const;

/** The canonical HR fields a connection maps (spec 019 section 2); the record ID and status are required. */
export const HR_FIELDS = [
  "record_id",
  "status",
  "work_email",
  "employee_number",
  "name_en",
  "name_ar",
  "hire_date",
  "termination_date",
  "job_title",
  "grade",
  "employment_type",
  "cost_centre",
  "location",
  "org_unit_code",
  "manager_employee_number",
  "assignment_date",
  "personal_email",
] as const;
export const HR_REQUIRED = ["record_id", "status"] as const;
export const HR_STATUSES = ["active", "terminated"] as const;
export const HR_PAGING = ["none", "page", "next"] as const;
export const HR_AUTH = ["api_key", "bearer", "basic", "oauth_client_credentials"] as const;
export const HR_PACK_KINDS = ["hr_rest", "hr_sftp"] as const;

/** Mapping shared by the HR REST and SFTP packs (spec 019 FR-016). */
export type HrMappingSettings = {
  fields: Record<string, string> | null;
  statusValues: Record<string, string[]> | null;
  dateFormat: string | null;
  syncAt: string | null;
  timeZone: string | null;
};
export type HrRestSettings = HrMappingSettings & {
  employeesPath: string | null;
  employeePath?: string | null;
  employeeRecordPath?: string | null;
  payslipsPath?: string | null;
  payslipRecordsPath?: string | null;
  payslipFields?: Record<string, string> | null;
  payslipPdfPath?: string | null;
  leaveBalancesPath?: string | null;
  leaveBalanceRecordsPath?: string | null;
  leaveBalanceFields?: { type?: string; balance?: string } | null;
  leaveSubmitPath?: string | null;
  leaveReferencePath?: string | null;
  leaveIdempotencyHeader?: string | null;
  dataChangePath?: string | null;
  dataChangeReferencePath?: string | null;
  dataChangeIdempotencyHeader?: string | null;
  leaveTypeCodes?: Record<string, string> | null;
  recordsPath: string | null;
  paging: string | null;
  pageParam: string | null;
  pageSizeParam: string | null;
  pageSize: number | null;
  nextPath: string | null;
};
export type HrSftpSettings = HrMappingSettings & {
  sftp: ClockSettings["sftp"];
  delimiter: string | null;
  hasHeader: boolean | null;
};

export type HrProblem = { recordId: string | null; reason: string };
export type HrRun = {
  id: string;
  connectionId: string;
  status: "applied" | "held" | "confirmed" | "discarded" | "superseded";
  startedAt: string;
  finishedAt: string | null;
  total: number;
  matched: number;
  unmatched: number;
  conflicts: number;
  refused: number;
  changed: number;
  terminations: number;
  moves: number;
  activeBefore: number;
  problems: HrProblem[];
  decidedBy: string | null;
  decidedAt: string | null;
};
export type HrOverview = {
  settings: { matchRule: string[]; changeThresholdPercent: number; version: number; onboardingLeadDays: number; rehireReactivates: boolean };
  connection: { id: string; kind: string; displayName: string; enabled: boolean } | null;
  counts: { matched: number; unmatched: number; stale: number; conflicts: number };
  lastRun: HrRun | null;
  heldRun: HrRun | null;
};
export const HR_STATES = ["unmatched", "stale", "conflict", "matched"] as const;
export const HR_MATCH_RULES = ["work_email", "employee_number"] as const;
export const HR_PROBLEMS = ["missing_id", "duplicate_id", "bad_status", "bad_date", "bad_email", "too_long", "manager_loop", "self_manager"] as const;
export type HrRecord = {
  recordId: string;
  employeeNumber: string | null;
  workEmail: string | null;
  nameEn: string | null;
  nameAr: string | null;
  state: string;
  matchRule: string | null;
  userId: string | null;
  userName: string | null;
  firstSeenAt: string;
  lastSeenAt: string;
  lastRunId: string;
};
export type HrRecordPage = { items: HrRecord[]; nextCursor: string | null };

/** Lifecycle changes HR reported (spec 019 US2 to US4) and the org codes HR uses (FR-007). */
export const HR_CHANGE_KINDS = ["hire", "assignment", "termination", "rehire"] as const;
export const HR_CHANGE_STATUSES = ["unmapped", "pending", "applied", "cancelled"] as const;
export const HR_CANCEL_REASONS = ["replaced", "reverted", "rescinded", "cancelled_in_hr"] as const;
export type HrChange = {
  id: string;
  recordId: string;
  userId: string | null;
  userName: string | null;
  kind: string;
  effectiveDate: string;
  dueAt: string;
  status: string;
  details: Record<string, string | null>;
  runId: string;
  createdAt: string;
  appliedAt: string | null;
  cancelledReason: string | null;
};
export type HrOrgCode = {
  code: string;
  unitId: string | null;
  unitNames: Record<string, string> | null;
  people: number;
  waiting: number;
  firstSeenAt: string;
  lastSeenAt: string;
};

/** Where each payslip value is in payroll's answer (spec 019 US6). */
export const HR_PAYSLIP_FIELDS = ["period", "gross", "deductions", "net", "currency"] as const;

/** The leave balance source (spec 019 US7, FR-013): TeamBeats keeps balances, or HR does. */
export const HR_LEAVE_SOURCES = ["teambeats", "hr"] as const;
export type HrLeaveSetting = { source: string; allowStaleSubmit: boolean; version: number; fromHr: boolean };

export const HR_LEAVE_HANDOFF_STATES = ["queued", "recorded", "failed"] as const;
export const HR_LEAVE_HANDOFF_KINDS = ["approved", "cancelled", "changed"] as const;
export type HrLeaveHandOff = {
  id: string;
  requestId: string;
  change: number;
  kind: string;
  state: string;
  attempt: number;
  externalReference: string | null;
  errorClass: string | null;
  createdAt: string;
  recordedAt: string | null;
  nextRetryAt: string | null;
};
