/** Shapes of the spec 026 security API. The API decides every rule; these types only carry its answers. */
export type SecurityPolicy = {
  ipAllow: string[];
  ipBlock: string[];
  adminIpAllow: string[];
  networkZones: Record<string, string[]>;
  requireMfa: boolean;
  mfaClaimSeen: boolean;
  stepUpMinutes: number;
  idleMinutes: number;
  absoluteHours: number;
  maxSessions: number | null;
  rememberDeviceDays: number;
  /** Offline reading on devices (spec 030 US3): items, megabytes and days a device may keep. */
  offlineMaxItems: number;
  offlineMaxMb: number;
  offlineDays: number;
  guestAllowDomains: string[];
  guestBlockDomains: string[];
  scopedRulesAvailable: boolean;
  yourAddress: string | null;
  version: number;
};

export type LockoutPreview = { peopleOutside: number; yourAddress: string | null; youWouldBeRefused: boolean };

export type SecurityAlert = {
  id: string;
  kind: string;
  subjectUserId: string | null;
  subjectName: string | null;
  facts: Record<string, unknown>;
  status: "open" | "handled" | "dismissed";
  handledBy: string | null;
  handledByName: string | null;
  handledAt: string | null;
  note: string | null;
  createdAt: string;
  updatedAt: string;
};

export type PostureCheck = { key: string; state: "passed" | "failed" | "unavailable"; link: string | null };
export type Posture = { passed: number; total: number; checks: PostureCheck[] };

/** The answer a sensitive action gets when the sign-in is too old (spec 026 section 6). */
export type StepUpProblem = { code: "security.step_up_required"; stepUpUrl: string; stepUpMinutes: number };

/** Sends the user for a fresh sign-in when the API asks for one, and returns to this page afterwards. */
export async function stepUpIfAsked(response: Response, returnTo: string): Promise<boolean> {
  if (response.status !== 401) return false;
  try {
    const problem = (await response.clone().json()) as Partial<StepUpProblem>;
    if (problem.code === "security.step_up_required" && problem.stepUpUrl) {
      // The fresh sign-in starts at the API (an OIDC redirect), not at a Next.js page.
      // eslint-disable-next-line @next/next/no-location-assign-relative-destination
      window.location.assign(`${problem.stepUpUrl}&returnTo=${encodeURIComponent(returnTo)}`);
      return true;
    }
  } catch {
    // Not a step-up answer.
  }
  return false;
}

/** One range per line or comma, as typed. */
export function ranges(text: string): string[] {
  return text
    .split(/[\n,]/)
    .map((r) => r.trim())
    .filter(Boolean);
}

/** A SIEM destination (spec 026 US4). `target` is the URL or host and port; `secretRef` is a secret manager reference. */
export type SiemDestination = {
  id: string;
  name: string;
  transport: "https" | "syslog";
  target: string;
  secretRef: string | null;
  enabled: boolean;
  position: number;
  streamEnd: number;
  lastSuccessAt: string | null;
  failingSince: string | null;
  lastError: string | null;
  version: number;
  createdAt: string;
};

export type SiemTestResult = { delivered: boolean; errorClass: string | null };

/** The SIEM page: the destinations, and whether this person may change them (the API checks every change again). */
export type SiemPage = { destinations: SiemDestination[]; canManage: boolean };

/** One item a reviewer decides (spec 026 FR-012): a guest in a space, or an administrator's role. */
export type AccessReviewItem = {
  id: string;
  kind: "guest" | "admin_role";
  subjectId: string;
  subjectName: string;
  spaceId: string | null;
  spaceName: string | null;
  roleKey: string | null;
  scopeType: string | null;
  scopeId: string | null;
  decision: "confirmed" | "removed" | "expired" | "kept" | null;
  dueAt: string;
};

export type AccessReviewSummary = {
  id: string;
  openedAt: string;
  dueAt: string;
  closedAt: string | null;
  open: number;
  confirmed: number;
  removed: number;
  expired: number;
  kept: number;
};

export type AccessReviewsPage = { reviews: AccessReviewSummary[]; nextAt: string | null; canStart: boolean };

/** Tenant alert thresholds (spec 026 FR-013); editable says whether this person may change them on this plan. */
export type AlertSettings = {
  downloadFiles: number;
  downloadBytes: number;
  workStart: string;
  workEnd: string;
  apiDeniedCount: number;
  apiVolumeFactor: number;
  guestConfidentialNotify: "security" | "sponsor" | "both";
  editable: boolean;
};
