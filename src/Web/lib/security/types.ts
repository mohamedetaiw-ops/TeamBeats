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
