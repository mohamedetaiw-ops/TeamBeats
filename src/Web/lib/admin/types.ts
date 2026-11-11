/**
 * Shapes of the R0 administration APIs (specs 001 to 006) as the admin screens read them. The API decides every rule
 * and every permission; these types only describe its answers.
 */

export type TenantSettings = {
  displayName: string;
  defaultLocale: string;
  enabledLocales: string[];
  timeZone: string;
  numberingSystem: "latn" | "arab" | string;
  primaryColor: string | null;
  hasLogo: boolean;
  rowVersion: number;
};

export type AdminAppointment = { id: string; email: string; status: string; userId: string | null; createdAt: string };

export type OrgUnitView = {
  id: string;
  parentId: string | null;
  typeKey: string;
  code: string;
  status: "active" | "inactive" | string;
  names: Record<string, string>;
  level: number;
  owner: "product" | "directory" | string;
};

export type UnitType = { key: string; builtIn: boolean; names: Record<string, string> | null };

export type OrgMembership = {
  id: string;
  userId: string;
  orgUnitId: string;
  type: string;
  isPrimary: boolean;
  validFrom: string;
  validTo: string | null;
};

export const MEMBERSHIP_TYPES = ["primary", "secondary", "secondment", "other"] as const;

export type UserSummary = {
  id: string;
  displayName: string;
  email: string;
  status: string;
  membershipStatus: string | null;
  lastSignInAt: string | null;
};

/** The API sends some enums by position; these lists name them in that order. */
export const SCOPE_TYPES = ["tenant", "org_unit", "org_subtree", "resource"] as const;
const MEMBERSHIP_STATUSES = ["active", "disabled"] as const;

export function scopeTypeName(value: string | number | null | undefined): string {
  if (typeof value === "number") return SCOPE_TYPES[value] ?? "tenant";
  return value ?? "tenant";
}

export function membershipStatusName(value: string | number | null | undefined): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value === "number") return MEMBERSHIP_STATUSES[value] ?? null;
  return value.toLowerCase();
}

export type UserDetail = {
  user: { id: string; displayName: string; email: string; status: string; preferredLocale: string | null; lastSignInAt: string | null };
  tenantMembership: { userId: string; membershipType: string; status: string | number; preferredOrgUnitId: string | null } | null;
  organizationMemberships: { membershipId: string; orgUnitId: string; type: string; isPrimary: boolean }[];
  roleAssignments: { assignmentId: string; roleKey: string; scopeType: string | number; scopeId: string | null }[];
};

export type RoleView = { id: string; key: string; isBuiltIn: boolean; isDelegable: boolean; permissions: string[]; names?: Record<string, string> | null };

/** A custom role's name in the viewer's language, then English, then any name it has; null when it has none. */
export function customRoleName(role: Pick<RoleView, "names">, locale: string): string | null {
  const names = role.names ?? {};
  return names[locale] || names.en || Object.values(names).find(Boolean) || null;
}

export type AssignmentView = {
  id: string;
  subjectType: "user" | "group" | string;
  subjectId: string;
  roleKey: string;
  scopeType: string;
  scopeId: string | null;
  createdAt: string;
  condition?: unknown;
};

export type DecisionReason = {
  kind: string;
  roleKey?: string | null;
  scopeType?: string | number | null;
  scopeId?: string | null;
  rule?: string | null;
  subject?: string | null;
};

export type ExplainResult = { allowed: boolean; reasons: DecisionReason[]; notApplied: DecisionReason[] };

export type Entitlements = {
  packageKey: string;
  version: number;
  startsAt: string;
  capabilities: Record<string, { enabled: boolean; limit: number | null }>;
};

export type GroupRoleMapping = { groupId: string; roleKey: string; scopeType: string; scopeId: string | null };

export type IdentityProvider = {
  id: string;
  kind: string;
  issuer: string;
  clientId: string;
  secretRef: string;
  displayName: string | null;
  jitEnabled: boolean;
  jitDomains: string[];
  groupRoleMap: GroupRoleMapping[];
  enabled: boolean;
};

export type DirectorySettings = {
  connectionId: string;
  userScope: { mode: string; groups: string[] };
  syncGroups: string[];
  scheduleMinutes: number;
  deletionThresholdPct: number;
  lastRunAt: string | null;
  nextRunAt: string;
  consecutiveFailures: number;
  source: string;
};

export type SyncRun = {
  id: string;
  startedAt: string;
  finishedAt: string | null;
  outcome: string;
  reason: string | null;
  created: number;
  updated: number;
  disabled: number;
  reenabled: number;
  groups: number;
  skipped: number;
};

export type DirectoryView = { connectionId: string | null; connectionUsable: boolean; settings: DirectorySettings | null; runs: SyncRun[] };

export type AuditEvent = {
  id: string;
  occurredAt: string;
  actorId: string | null;
  actorType: string;
  action: string;
  targetType: string;
  targetId: string | null;
  targetOrgUnitId: string | null;
  outcome: string;
  correlationId: string | null;
  changes: unknown;
  chainPosition: number | null;
};

export type AuditPage = { items: AuditEvent[]; nextCursor: string | null };

export type AuditRetention = { days: number; minimumDays: number };

export type AuditIntegrity = { checked: number; firstBreak: number | null; sealedThrough: number; unsealed: number; intact: boolean };

export type AuditExport = {
  id: string;
  format: "csv" | "json" | string;
  status: string;
  rowCount: number | null;
  createdAt: string;
  completedAt: string | null;
  expiresAt: string | null;
};

/** The unit's name in the viewer's language, then English, then any name it has. */
export function unitLabel(unit: { names: Record<string, string>; code?: string }, locale: string): string {
  return unit.names[locale] ?? unit.names.en ?? Object.values(unit.names)[0] ?? unit.code ?? "";
}

/** The part of a permission or capability key before its first dot, used to group long lists into areas. */
export function areaOf(key: string): string {
  return key.split(".")[0] ?? key;
}

/** Groups keys by area, in a stable order. */
export function byArea<T>(items: readonly T[], key: (item: T) => string): [string, T[]][] {
  const groups = new Map<string, T[]>();
  for (const item of items) {
    const area = areaOf(key(item));
    groups.set(area, [...(groups.get(area) ?? []), item]);
  }
  return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
}
