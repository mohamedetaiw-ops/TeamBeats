/** Shapes of the API and MCP access API (spec 035). The API decides who may read or change each of them. */

export type AppKind = "application" | "delegated_web" | "delegated_public" | "mcp_client";

export const appKinds: readonly AppKind[] = ["application", "delegated_web", "delegated_public", "mcp_client"];

export type ApiApp = {
  id: string;
  clientId: string;
  kind: AppKind;
  name: string;
  description: string | null;
  publisher: string | null;
  scopes: string[];
  redirectUris: string[];
  allowGuests: boolean;
  status: "active" | "disabled";
  createdAt: string;
  lastUsedAt: string | null;
  version: number;
  ipRanges: string[];
  callsPerMinute: number;
  callsPerMinuteLowered: boolean;
};

export type ApiCredential = {
  id: string;
  kind: "secret" | "public_key";
  hint: string | null;
  expiresAt: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
};

export type ApiAppDetail = { app: ApiApp; credentials: ApiCredential[]; adminConsentScopes: string[] };

export type IssuedCredential = { credential: ApiCredential; secret: string | null };

export type ApiScope = { key: string; descriptionKey: string; userConsent: boolean; holder: "delegated" | "application" | "both"; capabilities: string[] };

export type ApiSettings = {
  accessTokenMinutes: number;
  refreshTokenDays: number;
  userConsentScopes: string[];
  apiEnabled: boolean;
  version: number;
  tenantCallsPerMinute: number | null;
};

export type AppUsage = {
  appId: string;
  name: string;
  kind: AppKind;
  calls: number;
  errors: Record<string, number>;
  denied: number;
  rateLimited: number;
  topOperations: { operation: string; calls: number }[];
  lastCallAt: string | null;
};

export type ApiUsage = { days: number; monthCalls: number; monthlyLimit: number | null; apps: AppUsage[] };

export type AdminConsent = {
  consentId: string;
  appId: string;
  appName: string;
  userId: string | null;
  scopes: string[];
  grantedBy: string;
  grantedAt: string;
  lastUsedAt: string | null;
};

export type ConnectedApp = {
  consentId: string;
  appId: string;
  name: string;
  publisher: string | null;
  scopes: string[];
  grantedAt: string;
  lastUsedAt: string | null;
};

export type AppRoleAssignment = { id: string; subjectType: string; subjectId: string; roleKey: string; scopeType: string; scopeId: string | null; createdAt: string };

export type { RoleView as RoleSummary } from "@/lib/admin/types";

/** Whether the app acts for a signed-in person, so it needs consent and redirect addresses. */
export function isDelegated(kind: AppKind): boolean {
  return kind !== "application";
}

/** Whether the app keeps a credential: apps that act as themselves and web apps with a server. */
export function isConfidential(kind: AppKind): boolean {
  return kind === "application" || kind === "delegated_web";
}

/** Scopes an app of this kind may hold, from the catalogue. */
export function scopesFor(kind: AppKind, scopes: readonly ApiScope[]): ApiScope[] {
  return scopes.filter((s) => s.holder === "both" || (kind === "application" ? s.holder === "application" : s.holder === "delegated"));
}

/** The resource key of a scope's description, without the namespace the API includes. */
export function scopeLabelKey(scope: string): string {
  return scope.replace(":", "_");
}

/** The platform default of an app's call limit per minute, which a tenant may only lower (spec 035 section 6). */
export function defaultCallsPerMinute(kind: AppKind): number {
  return kind === "application" ? 600 : 120;
}
