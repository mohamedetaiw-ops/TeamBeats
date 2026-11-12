"use client";

/**
 * Web Push on this device (spec 030 US2, FR-006 to FR-008). The server holds the subscription and decides what to send
 * (spec 015 push channel); this file only asks the browser for a subscription, hands it to the API and forgets it.
 */

/** Push availability for this person, as GET /api/v1/me/push answers. */
export type PushStatus = { available: boolean; publicKey: string | null; devices: number };

/**
 * The one event modules dispatch after a meaningful action, such as a submitted leave or service request, so the push
 * offer can appear (FR-006). Modules never import the offer; they only dispatch this event.
 */
export const meaningfulActionEvent = "tb:meaningful-action";

export function announceMeaningfulAction(): void {
  if (typeof window !== "undefined") window.dispatchEvent(new CustomEvent(meaningfulActionEvent));
}

/** A declined offer stays declined on this device for 90 days (FR-006). */
export const declineDays = 90;
const declineKey = (owner: string) => `tb:push-declined:${owner}`;

export function declinedRecently(owner: string, now = Date.now()): boolean {
  try {
    const at = Number(window.localStorage.getItem(declineKey(owner)) ?? "");
    return Number.isFinite(at) && at > 0 && now - at < declineDays * 86_400_000;
  } catch {
    return false;
  }
}

export function rememberDecline(owner: string, now = Date.now()): void {
  try {
    window.localStorage.setItem(declineKey(owner), String(now));
  } catch {
    // Without storage the offer may come back after the next meaningful action, which is still only once per visit.
  }
}

/** True when this browser can receive Web Push at all. iOS before 16.4, and Safari outside an installed app, cannot. */
export function pushSupported(): boolean {
  return typeof window !== "undefined" && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
}

/** The platform for opt-in telemetry: a coarse family, never a device ID. */
export function platform(): string {
  const ua = navigator.userAgent;
  if (/Android/i.test(ua)) return "android";
  if (/iPhone|iPad|iPod/i.test(ua)) return "ios";
  if (/Windows/i.test(ua)) return "windows";
  if (/Macintosh|Mac OS X/i.test(ua)) return "macos";
  if (/Linux|CrOS/i.test(ua)) return "linux";
  return "other";
}

function keyBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const s = base64url.replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(s + "=".repeat((4 - (s.length % 4)) % 4));
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

export async function deviceSubscription(): Promise<PushSubscription | null> {
  if (!pushSupported()) return null;
  const registration = await navigator.serviceWorker.getRegistration();
  return (await registration?.pushManager.getSubscription()) ?? null;
}

export type TurnOnResult = "on" | "denied" | "unsupported" | "failed";

/** Asks the browser for permission, subscribes with the tenant's VAPID key and registers the device with the API. */
export async function turnOnPush(publicKey: string): Promise<TurnOnResult> {
  if (!pushSupported()) return "unsupported";
  try {
    const permission = await Notification.requestPermission();
    if (permission !== "granted") return "denied";
    const registration = await navigator.serviceWorker.ready;
    let subscription = await registration.pushManager.getSubscription();
    // A subscription made for another key (a rotated VAPID pair) can never receive our messages.
    const current = subscription?.options.applicationServerKey;
    if (subscription && current && !sameKey(new Uint8Array(current), keyBytes(publicKey))) {
      await subscription.unsubscribe();
      subscription = null;
    }
    subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
    const json = subscription.toJSON();
    const response = await fetch("/api/v1/me/push/subscriptions", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ endpoint: json.endpoint, keys: { p256dh: json.keys?.p256dh, auth: json.keys?.auth }, platform: platform() }),
    });
    if (response.ok) return "on";
    await subscription.unsubscribe().catch(() => undefined);
    return "failed";
  } catch {
    return "failed";
  }
}

function sameKey(a: Uint8Array, b: Uint8Array): boolean {
  return a.length === b.length && a.every((v, i) => v === b[i]);
}

/** Removes this device: from the API first, then from the browser, so no message is sent to a forgotten device. */
export async function turnOffPush(): Promise<boolean> {
  const subscription = await deviceSubscription().catch(() => null);
  if (!subscription) return true;
  try {
    const response = await fetch("/api/v1/me/push/subscriptions:remove", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ endpoint: subscription.endpoint }),
    });
    if (!response.ok) return false;
    await subscription.unsubscribe();
    return true;
  } catch {
    return false;
  }
}

/**
 * On sign-out the server drops the session's devices (FR-008); the browser forgets its subscription too, so the next
 * person to sign in on this device starts with push off.
 */
export async function forgetDevicePush(): Promise<void> {
  try {
    await (await deviceSubscription())?.unsubscribe();
  } catch {
    // Nothing is sent to it anyway once the server removed it.
  }
}
