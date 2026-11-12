"use client";

import { forgetDevicePush } from "@/lib/push";
import { wipeOffline } from "@/lib/offline";

/**
 * Leaves nothing of the last person on a shared kiosk (spec 030 US4 scenario 2, FR-016): browser storage, form drafts
 * kept there, the offline cache, the push subscription and any cache other than the app's own shell files.
 */
export async function wipeDevice(): Promise<void> {
  try {
    sessionStorage.clear();
  } catch {
    // Storage is off: nothing was kept.
  }
  try {
    localStorage.clear();
  } catch {
    // Storage is off: nothing was kept.
  }
  await Promise.all([wipeOffline(), forgetDevicePush()]);
  try {
    const names = await caches.keys();
    await Promise.all(names.filter((name) => !name.startsWith("tb-shell-")).map((name) => caches.delete(name)));
  } catch {
    // No cache storage here.
  }
}

/** Ends the kiosk session on the server, wipes the device and goes back to the kiosk's start screen. */
export async function endKioskSession(): Promise<void> {
  await fetch("/api/v1/auth/signout", { method: "POST" })
    .then((r) => r.text())
    .catch(() => "");
  await wipeDevice();
  window.location.replace("/kiosk");
}
