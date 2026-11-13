/** Shapes of the spec 016 services, requests and task inbox API, as the browser reads them. */
export type Names = Record<string, string>;

export type Category = { id: string; names: Names; position: number };

export type ServiceKind = "form" | "linked" | "module";

export type ServiceCard = {
  id: string;
  kind: ServiceKind;
  names: Names;
  descriptions: Names;
  categoryId: string | null;
  url: string | null;
  sso: boolean;
  launchRoute: string | null;
  favourite: boolean;
  /** The service's own icon (spec 016 FR-018); null takes its category's. */
  icon?: string | null;
  /** For a form service, who approves (FR-019): "none", "line_manager", "role" or "named". */
  approver?: "none" | "line_manager" | "role" | "named" | null;
  /** The working days the approval step allows. */
  dueWorkingDays?: number | null;
  /** The viewer's latest request of this service. */
  latest?: { id: string; number: string; status: string } | null;
};

export type CatalogueView = { categories: Category[]; services: ServiceCard[]; recent: string[] };

export type FieldType =
  | "text"
  | "number"
  | "money"
  | "date"
  | "date_range"
  | "choice"
  | "multi_choice"
  | "person"
  | "org_unit"
  | "lookup"
  | "attachment"
  | "policy_note";

export const fieldTypes: FieldType[] = [
  "text",
  "number",
  "money",
  "date",
  "date_range",
  "choice",
  "multi_choice",
  "person",
  "org_unit",
  "lookup",
  "attachment",
  "policy_note",
];

export const prefillSources = [
  "profile.display_name",
  "profile.email",
  "profile.job_title",
  "profile.department",
  "profile.office",
  "profile.work_phone",
  "profile.mobile_phone",
  "profile.employee_number",
  "profile.manager",
  "org.primary_unit",
  "org.company",
  "hr.employee_number",
  "hr.job_title",
  "hr.grade",
  "hr.cost_centre",
  "hr.employment_type",
  "hr.location",
  "hr.hire_date",
] as const;

/** HR sources (spec 019 US5) can keep the HR value and can ask for one HR confirmed in the last 7 days. */
export const isHrSource = (source: string | null | undefined) => typeof source === "string" && source.startsWith("hr.");

/** The sources offered for a field type; the API checks the same. */
export const prefillSourcesFor = (type: FieldType) =>
  prefillSources.filter((p) =>
    p === "profile.manager" ? type === "person" : p === "org.primary_unit" || p === "org.company" ? type === "org_unit" : p === "hr.hire_date" ? type === "date" : type === "text",
  );

export type FieldOption = { value: string; labels: Names };
export type FieldRule = { kind: string; value?: number | null; message?: Names | null };
export type FieldCondition = { field: string; values: string[] };

export type FormField = {
  key: string;
  type: FieldType;
  labels: Names;
  help?: Names | null;
  required?: boolean;
  options?: FieldOption[] | null;
  prefill?: string | null;
  rules?: FieldRule[] | null;
  showWhen?: FieldCondition | null;
  note?: Names | null;
  currencies?: string[] | null;
  readOnly?: boolean;
  mustBeCurrent?: boolean;
};

export type FormDefinition = { fields: FormField[] };

export type ServiceDetail = {
  service: ServiceCard;
  version: number | null;
  form: FormDefinition;
  allowOnBehalf: boolean;
  maxAttachments: number;
  hasApproval: boolean;
  classification: string;
};

export type FieldValue = string | number | string[] | { amount: number; currency: string } | { from: string; to: string } | null;

export type Prefill = { values: Record<string, FieldValue>; readOnly: string[]; unconfirmed?: string[] };

/** A request an AI agent prepared for the person (spec 020 US8); the form opens with its values. */
export type PreparedRequest = { id: string; serviceId: string; data: Record<string, FieldValue>; status: "prepared" | "submitted" | "discarded" };

export type PersonRef = { id: string; displayName: string };

export type RequestStatus = "draft" | "submitted" | "in_approval" | "returned" | "approved" | "rejected" | "cancelled" | "completed";

export const requestStatuses: RequestStatus[] = ["submitted", "in_approval", "returned", "approved", "rejected", "cancelled", "completed"];

export type RequestSummary = {
  id: string;
  number: string;
  serviceId: string;
  serviceNames: Names;
  status: RequestStatus;
  requester: PersonRef;
  onBehalfOf: PersonRef | null;
  currentStep: Names | null;
  assignees: PersonRef[];
  createdAt: string;
  submittedAt: string | null;
  closedAt: string | null;
};

export type TrackingEntry = { event: string; step: string | null; actor: PersonRef | null; onBehalfOf: PersonRef | null; comment: string | null; at: string };

export type AttachmentView = { id: string; fileName: string; sizeBytes: number; contentType: string; scanState: "pending" | "clean" | "infected" | "failed" };

export type RequestDetail = {
  request: RequestSummary;
  serviceVersion: number;
  form: FormDefinition;
  data: Record<string, FieldValue>;
  classification: string;
  attachments: AttachmentView[];
  tracking: TrackingEntry[];
  automation: RequestAutomation | null;
};

/** States of a connector step of a request (spec 016 US8, spec 017 T009). */
export const AUTOMATION_STATES = ["running", "succeeded", "business_error", "incident", "skipped", "cancelled"] as const;

export type AutomationStep = {
  elementId: string;
  name: string;
  status: string;
  externalReference: string | null;
  errorClass: string | null;
  errorCode: string | null;
  startedAt: string;
  endedAt: string | null;
  incidentId: string | null;
  names: Names | null;
};

/** The automatic steps of a request; canOperate comes from the API (service owners), never from a role check here. */
export type RequestAutomation = { steps: AutomationStep[]; canOperate: boolean };

export type InboxItem = {
  taskId: string;
  source: string;
  name: string;
  step: Names | null;
  requestId: string | null;
  number: string | null;
  serviceId: string | null;
  serviceNames: Names | null;
  requester: PersonRef | null;
  requestedFor: PersonRef | null;
  onBehalfOf: PersonRef | null;
  createdAt: string;
  dueAt: string | null;
  /** Set for tasks of other modules, such as content reviews (spec 009). */
  title?: Names | null;
  link?: string | null;
};

export type Delegation = { delegate: PersonRef; startsOn: string; endsOn: string; active: boolean };

export type ServiceAudience = { orgUnits?: string[] | null; groups?: string[] | null; membershipTypes?: string[] | null };

export type ServiceRow = {
  id: string;
  kind: ServiceKind;
  moduleKey: string | null;
  names: Names;
  descriptions: Names;
  categoryId: string | null;
  audience: ServiceAudience;
  ownerOrgUnitId: string | null;
  classification: string;
  url: string | null;
  sso: boolean;
  launchRoute: string | null;
  status: "draft" | "published" | "retired";
  currentVersion: number | null;
  updatedAt: string;
  icon?: string | null;
};

export type ApproverRule = { kind: "users" | "permission_at_unit" | "line_manager"; users?: string[] | null; permission?: string | null };
export type ApprovalStep = { names: Names | null; approvers: ApproverRule; dueWorkingDays?: number | null };
/**
 * A connector step after the approval. inputs maps each action field to a request value: form.{field}, request.number,
 * requester.name or requester.email. onBusinessError maps declared error codes to continue or reject.
 */
export type ActionStep = {
  names: Names | null;
  connectionId: string;
  action: string;
  inputs: Record<string, string>;
  onBusinessError?: Record<string, "continue" | "reject"> | null;
};
export type ServiceRoute = { approval: ApprovalStep | null; actions?: ActionStep[] | null };

export const MAX_ACTION_STEPS = 5;
export const REQUEST_VALUES = ["request.number", "requester.name", "requester.email"] as const;

export type RouteActionInput = { name: string; type: string; required: boolean };
export type RouteAction = {
  connectionId: string;
  connectionName: string;
  action: string;
  sideEffect: string;
  idempotent: boolean;
  businessErrors: string[];
  inputs: RouteActionInput[];
  enabled: boolean;
};
export type RouteActionsView = { available: boolean; actions: RouteAction[] };

export type VersionRow = {
  version: number;
  state: "draft" | "published";
  form: FormDefinition;
  route: ServiceRoute;
  fulfilmentQueueGroupId: string | null;
  allowOnBehalf: boolean;
  maxAttachments: number;
  updatedAt: string;
  publishedAt: string | null;
};

export type ServiceAdminView = { service: ServiceRow; latest: VersionRow | null };

export type ServicePreview = {
  version: number;
  state: string;
  form: FormDefinition;
  prefill: Prefill;
  approvalStep: Names | null;
  allowOnBehalf: boolean;
  maxAttachments: number;
};

/** The text in the viewer's language, then English, then any language the owner filled in. */
export function pick(names: Names | null | undefined, locale: string, fallback = ""): string {
  if (!names) return fallback;
  return names[locale] || names.en || Object.values(names).find(Boolean) || fallback;
}

/** Whether a field is shown: always, or when the earlier choice field it depends on has one of the values (FR-003). */
export function isShown(form: FormDefinition, field: FormField, data: Record<string, FieldValue>): boolean {
  const when = field.showWhen;
  if (!when) return true;
  const value = data[when.field];
  if (Array.isArray(value)) return value.some((v) => when.values.includes(v));
  return typeof value === "string" && when.values.includes(value);
}
