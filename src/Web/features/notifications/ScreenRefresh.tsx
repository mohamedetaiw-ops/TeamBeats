"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

/**
 * Asks the server for the screen's feed again every few seconds (spec 015 FR-021), so a withdrawn announcement, or a
 * revoked or replaced address, is gone from the screen within a minute. Renders nothing.
 */
export function ScreenRefresh({ seconds }: { seconds: number }) {
  const router = useRouter();
  useEffect(() => {
    const every = Math.min(Math.max(seconds, 10), 30) * 1000;
    const timer = window.setInterval(() => router.refresh(), every);
    return () => window.clearInterval(timer);
  }, [router, seconds]);
  return null;
}
