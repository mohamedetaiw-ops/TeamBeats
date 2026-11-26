"use client";

/**
 * The web app inside the TeamBeats store app (spec 030 R3 T017, ADR-0023). The shell puts Capacitor's bridge on the
 * page; this file feature-detects it and reaches the plugins by name, so the web build imports no native package and
 * a browser never runs any of it. The server decides what is allowed; this only carries the device's answers.
 */

type PluginCall = (options?: Record<string, unknown>) => Promise<Record<string, unknown> | undefined>;
type Plugin = Record<string, PluginCall> & {
  addListener?: (event: string, handler: (data: Record<string, unknown>) => void) => Promise<{ remove(): Promise<void> }>;
};

type Bridge = { isNativePlatform(): boolean; getPlatform(): string; Plugins: Record<string, Plugin | undefined> };

function bridge(): Bridge | null {
  if (typeof window === "undefined") return null;
  const capacitor = (window as unknown as { Capacitor?: Bridge }).Capacitor;
  return capacitor?.isNativePlatform?.() ? capacitor : null;
}

/** True when this page runs in the store app on a phone. */
export function inShell(): boolean {
  return bridge() !== null;
}

/** The store platform, android or ios, for registering the push token. */
export function shellPlatform(): "android" | "ios" | null {
  const p = bridge()?.getPlatform();
  return p === "android" || p === "ios" ? p : null;
}

export function shellPlugin(name: string): Plugin | null {
  return bridge()?.Plugins[name] ?? null;
}

/** Calls a plugin method by name; a missing plugin or method answers undefined, as a browser would. */
async function call(plugin: Plugin | null, method: string, options?: Record<string, unknown>): Promise<Record<string, unknown> | undefined> {
  const fn = plugin?.[method];
  return fn ? fn.call(plugin, options) : undefined;
}

async function preference(key: string): Promise<string | null> {
  const value = (await call(shellPlugin("Preferences"), "get", { key }))?.value;
  return typeof value === "string" ? value : null;
}

async function setPreference(key: string, value: string | null): Promise<void> {
  const store = shellPlugin("Preferences");
  if (!store) return;
  if (value === null) await call(store, "remove", { key });
  else await call(store, "set", { key, value });
}

/** Native push in the shell: "blocked" when the person refused it in the phone's settings. */
export async function nativePushState(): Promise<"unsupported" | "blocked" | "off" | "on"> {
  const push = shellPlugin("PushNotifications");
  if (!push || !shellPlatform()) return "unsupported";
  const permission = (await call(push, "checkPermissions"))?.receive;
  if (permission === "denied") return "blocked";
  return permission === "granted" && (await preference("pushToken")) ? "on" : "off";
}

/** True once the phone has asked the person about notifications, so the push offer does not ask again. */
export async function nativePushAsked(): Promise<boolean> {
  const permission = (await call(shellPlugin("PushNotifications"), "checkPermissions"))?.receive;
  return permission === "granted" || permission === "denied";
}

/** The messaging token the phone gives after registering, or null after 15 seconds without one. */
function registrationToken(push: Plugin): Promise<string | null> {
  return new Promise((resolve) => {
    const handles: { remove(): Promise<void> }[] = [];
    const done = (token: string | null) => {
      clearTimeout(timer);
      handles.forEach((h) => void h.remove());
      resolve(token);
    };
    const timer = setTimeout(() => done(null), 15_000);
    void push.addListener?.("registration", (data) => done(typeof data.value === "string" ? data.value : null)).then((h) => handles.push(h));
    void push.addListener?.("registrationError", () => done(null)).then((h) => handles.push(h));
    void call(push, "register");
  });
}

/** Asks the phone for permission and the messaging token, and registers the token with the API (MOB-022). */
export async function turnOnNativePush(): Promise<"on" | "denied" | "unsupported" | "failed"> {
  const push = shellPlugin("PushNotifications");
  const platform = shellPlatform();
  if (!push || !platform) return "unsupported";
  try {
    const permission = (await call(push, "requestPermissions"))?.receive;
    if (permission !== "granted") return "denied";
    const token = await registrationToken(push);
    if (!token) return "failed";
    const response = await fetch("/api/v1/me/push/native", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ token, platform }),
    });
    if (!response.ok) return "failed";
    await setPreference("pushToken", token);
    return "on";
  } catch {
    return "failed";
  }
}

/** Removes this phone from the API first, then forgets the token, so nothing is sent to a forgotten device. */
export async function turnOffNativePush(): Promise<boolean> {
  const token = await preference("pushToken").catch(() => null);
  if (!token) return true;
  try {
    const response = await fetch("/api/v1/me/push/native:remove", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ token }),
    });
    if (!response.ok) return false;
    await setPreference("pushToken", null);
    await call(shellPlugin("PushNotifications"), "unregister");
    return true;
  } catch {
    return false;
  }
}

/** On sign-out the server drops the session's devices; the phone forgets the token so the next person starts with push off. */
export async function forgetNativePush(): Promise<void> {
  try {
    await setPreference("pushToken", null);
    await call(shellPlugin("PushNotifications"), "unregister");
  } catch {
    // The server removed the device already.
  }
}

/** The lock the shell worked out at start: the stricter of the tenant policy and the managed configuration. */
export type ShellLock = { appLock: "off" | "optional" | "required"; appLockMinutes: number };

export async function shellLock(): Promise<ShellLock | null> {
  try {
    const value = await preference("lock");
    const parsed = value ? (JSON.parse(value) as ShellLock) : null;
    return parsed && ["off", "optional", "required"].includes(parsed.appLock) && Number.isInteger(parsed.appLockMinutes) ? parsed : null;
  } catch {
    return null;
  }
}

/** The person's own choice when the lock is optional. */
export async function lockChosen(): Promise<boolean> {
  return (await preference("lockChosen").catch(() => null)) === "true";
}

export async function chooseLock(on: boolean): Promise<void> {
  await setPreference("lockChosen", on ? "true" : null);
}

/** Asks the phone for the face, fingerprint or passcode. Never sends anything to the server. */
export async function verifyOnDevice(reason: string, cancel: string): Promise<boolean> {
  const biometric = shellPlugin("BiometricAuthNative") ?? shellPlugin("BiometricAuth");
  if (typeof biometric?.authenticate !== "function") return false;
  try {
    await call(biometric, "authenticate", { reason, cancelTitle: cancel, allowDeviceCredential: true, iosFallbackTitle: cancel, androidTitle: reason });
    return true;
  } catch {
    return false;
  }
}

/** Called when the app goes to the background and comes back. */
export async function onAppState(handler: (active: boolean) => void): Promise<() => void> {
  const app = shellPlugin("App");
  const handle = await app?.addListener?.("appStateChange", (data) => handler(data.isActive === true));
  return () => void handle?.remove();
}
