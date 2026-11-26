/**
 * The rules of the TeamBeats store app (spec 030 R3, MOB-021, MOB-023, MOB-024; ADR-0023), as plain functions with no
 * imports so the Node test runner checks them in CI and the shell and the web bridge apply the same ones.
 */

export type AppLock = "off" | "optional" | "required";

/** What `GET /api/v1/mobile/shell` answers for a workspace. */
export type ShellConfig = { available: boolean; appLock: AppLock; appLockMinutes: number; signInHosts: string[]; nativePush: boolean };

/** Managed app configuration from MDM: AppConfig on iOS, managed configurations on Android. Every key is optional. */
export type Managed = { workspaceHost?: unknown; lockWorkspace?: unknown; appLock?: unknown; appLockMinutes?: unknown };

const LOCKS: readonly AppLock[] = ["off", "optional", "required"];
const LABEL = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;

/**
 * The host a person typed or MDM set, in lower case, or null when it is not a plain DNS name with a dot. A scheme,
 * a path or a trailing dot are dropped; a port, credentials, an IP address or a single label are refused.
 */
export function workspaceHost(input: unknown): string | null {
  if (typeof input !== "string") return null;
  let text = input.trim().toLowerCase();
  if (text.startsWith("https://")) text = text.slice(8);
  else if (text.includes("://")) return null;
  text = text.split(/[/?#]/, 1)[0].replace(/\.$/, "");
  if (text.length === 0 || text.length > 253 || text.includes("@") || text.includes(":")) return null;
  const labels = text.split(".");
  if (labels.length < 2 || !labels.every((l) => LABEL.test(l))) return null;
  if (labels.every((l) => /^\d+$/.test(l))) return null;
  return text;
}

/** Where a navigation goes: loaded in the app, or opened in the system browser. */
export type Route = "app" | "browser";

/**
 * MOB-021: only https on the workspace host, the sign-in hosts of that workspace and the platform's start page load in
 * the app; anything else opens in the system browser. With the workspace locked by MDM the start page is not offered.
 */
export function route(address: string, workspace: string | null, signInHosts: readonly string[], startUrl: string, locked: boolean): Route {
  let url: URL;
  try {
    url = new URL(address);
  } catch {
    return "browser";
  }
  if (url.protocol !== "https:" || url.port !== "" || url.username !== "" || url.password !== "") return "browser";
  const host = url.hostname.toLowerCase();
  if (workspace !== null && host === workspace) return "app";
  if (workspace !== null && signInHosts.some((h) => h.toLowerCase() === host)) return "app";
  if (!locked) {
    const start = new URL(startUrl);
    if (host === start.hostname && url.pathname.startsWith(start.pathname)) return "app";
  }
  return "browser";
}

/** The lock to apply: the stricter of the tenant policy and the managed configuration (MOB-024), minutes in 0 to 60. */
export function effectiveLock(tenant: { appLock: AppLock; appLockMinutes: number }, managed: Managed): { appLock: AppLock; appLockMinutes: number } {
  const managedLock = LOCKS.includes(managed.appLock as AppLock) ? (managed.appLock as AppLock) : null;
  const lock = managedLock !== null && LOCKS.indexOf(managedLock) > LOCKS.indexOf(tenant.appLock) ? managedLock : tenant.appLock;
  const raw = typeof managed.appLockMinutes === "string" ? Number(managed.appLockMinutes) : managed.appLockMinutes;
  const managedMinutes = typeof raw === "number" && Number.isInteger(raw) && raw >= 0 && raw <= 60 ? raw : null;
  const minutes = Math.min(Math.max(0, Math.min(60, tenant.appLockMinutes)), managedMinutes ?? 60);
  return { appLock: lock, appLockMinutes: minutes };
}

/** MDM's `lockWorkspace`: true, or the strings "true" and "1" some consoles send. */
export function workspaceLocked(managed: Managed): boolean {
  return managed.lockWorkspace === true || managed.lockWorkspace === "true" || managed.lockWorkspace === "1";
}

/**
 * MOB-023: whether the app asks for the device biometrics or passcode when it comes back. Off never locks; optional
 * locks only when the person turned it on; required always does. It locks once the app was away for the set minutes,
 * at once with 0, and on a cold start when the lock applies.
 */
export function shouldLock(lock: { appLock: AppLock; appLockMinutes: number }, personTurnedOn: boolean, awaySince: number | null, now: number): boolean {
  if (lock.appLock === "off" || (lock.appLock === "optional" && !personTurnedOn)) return false;
  if (awaySince === null) return true;
  return now - awaySince >= lock.appLockMinutes * 60_000;
}
