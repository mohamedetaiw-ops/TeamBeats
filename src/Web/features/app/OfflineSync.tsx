"use client";

import { useEffect } from "react";
import { syncOffline, wipeOffline } from "@/lib/offline";

/** Pages mount the shell on every navigation; a sync at most every few minutes is enough to prune withdrawn items. */
const syncedKey = "tb:offline-synced";
const syncEvery = 5 * 60_000;

function recentlySynced(): boolean {
  try {
    return Date.now() - Number(sessionStorage.getItem(syncedKey) ?? "0") < syncEvery;
  } catch {
    return false;
  }
}

function markSynced(): void {
  try {
    sessionStorage.setItem(syncedKey, String(Date.now()));
  } catch {
    // Without storage the next page syncs again, which is only extra work.
  }
}

/**
 * Keeps this device's offline reading up to date (spec 030 US3): on the first page of a visit and every few minutes
 * after, and again when the connection comes back. It shows nothing; the offline page is where the kept items are read.
 */
export function OfflineSync() {
  useEffect(() => {
    const sync = (force: boolean) => {
      if (!force && recentlySynced()) return;
      void syncOffline()
        .then((result) => result === "synced" && markSynced())
        .catch(() => undefined);
    };
    sync(false);
    const online = () => sync(true);
    window.addEventListener("online", online);
    return () => window.removeEventListener("online", online);
  }, []);
  return null;
}

/**
 * On the sign-in page there is no session, so whatever an earlier session kept for offline reading is deleted (FR-011):
 * after sign-out, session expiry or revocation, the next person on this device starts with nothing.
 */
export function OfflineWipe() {
  useEffect(() => {
    try {
      sessionStorage.removeItem(syncedKey);
    } catch {
      // Nothing to forget.
    }
    void wipeOffline();
  }, []);
  return null;
}
