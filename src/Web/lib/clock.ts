"use client";

import { useSyncExternalStore } from "react";

const never = () => () => {};
const everyHalfMinute = (callback: () => void) => {
  const timer = setInterval(callback, 30_000);
  return () => clearInterval(timer);
};
const nowMinute = () => Math.floor(Date.now() / 60_000) * 60_000;
const none = () => null;

/**
 * The current time to the minute, or null while rendering on the server and hydrating, so text that depends on the
 * time ("3 minutes ago", today's date in the viewer's zone) never differs between the server and the browser.
 */
export function useNow(): number | null {
  return useSyncExternalStore(everyHalfMinute, nowMinute, none);
}

/** True once the browser has hydrated: the server render has no time zone of the viewer. */
export function useHydrated(): boolean {
  return useSyncExternalStore(never, () => true, () => false);
}
