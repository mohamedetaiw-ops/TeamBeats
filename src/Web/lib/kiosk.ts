/**
 * Kiosk and shared device mode (spec 030 US4). The API decides everything: which tablet is a kiosk, who may sign in,
 * what a kiosk session reaches. The app only shows the forms and wipes the device when a session ends.
 */

export type KioskDeviceInfo = { enrolled: boolean; name: string | null; location: string | null; idleMinutes: number };

export type KioskFeature = "announcements" | "policies" | "leave" | "shifts" | "services" | "booking";

/** The features an administrator can allow on a kiosk, in the order the API lists them. */
export const kioskFeatures: KioskFeature[] = ["announcements", "policies", "leave", "shifts", "services", "booking"];

export type KioskDevice = {
  id: string;
  name: string;
  orgUnitId: string;
  location: string | null;
  features: KioskFeature[];
  idleMinutes: number;
  allowOtherUnits: boolean;
  status: "waiting" | "enrolled" | "revoked";
  enrolExpiresAt: string | null;
  enrolledAt: string | null;
  lastSeenAt: string | null;
  createdAt: string;
  version: number;
};

export type KioskEnrolment = { device: KioskDevice; code: string; expiresAt: string };

export type Badge = { userId: string; issued: boolean; pinSet: boolean; issuedAt: string | null; lockedUntil: string | null };

export type BadgeIssued = { userId: string; code: string; qrSvg: string };

/** A refusal from the API: its key, so a form can take the next step, and its messages in the viewer's language. */
export type Refusal = { status: number; key: string | null; messages: string[] };

export type Outcome<T> = { ok: true; value: T } | { ok: false; refusal: Refusal };

export async function kioskCall<T>(url: string, method: "GET" | "POST" | "PUT", body?: unknown): Promise<Outcome<T>> {
  let response: Response;
  try {
    response = await fetch(url, {
      method,
      cache: "no-store",
      headers: body === undefined ? undefined : { "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    return { ok: false, refusal: { status: 0, key: null, messages: [] } };
  }
  if (response.ok) {
    return { ok: true, value: (response.status === 204 ? undefined : await response.json()) as T };
  }
  try {
    const problem = (await response.json()) as {
      detailKey?: string;
      detail?: string;
      title?: string;
      errors?: Record<string, { key: string; message: string }[]>;
    };
    const errors = Object.values(problem.errors ?? {}).flat();
    return {
      ok: false,
      refusal: {
        status: response.status,
        key: errors[0]?.key ?? problem.detailKey ?? null,
        messages: errors.length > 0 ? errors.map((e) => e.message) : [problem.detail ?? problem.title ?? ""].filter(Boolean),
      },
    };
  } catch {
    return { ok: false, refusal: { status: response.status, key: null, messages: [] } };
  }
}

/** A PIN is 4 to 8 digits (spec 030 section 6). */
export const validPin = (pin: string) => /^[0-9]{4,8}$/.test(pin);

/** What counts as someone still using the kiosk: their own input only, never a scroll the page makes itself. */
export const activityEvents = ["pointerdown", "keydown", "touchstart", "wheel"] as const;
