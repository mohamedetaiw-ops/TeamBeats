"use client";

import { use } from "react";
import type { PlacementProps } from "@/lib/experience/contract";

const waits = new Map<string, Promise<void>>();

/** Resolves after a few seconds. Kept only while pending, so each page load waits again. */
function slow(key: string): Promise<void> {
  let wait = waits.get(key);
  if (!wait) {
    wait = new Promise<void>((resolve) => setTimeout(resolve, 3000)).then(() => {
      waits.delete(key);
    });
    waits.set(key, wait);
  }
  return wait;
}

/** Reference pack alpha: fault injection for the renderer tests. Throws, or suspends for a few seconds. */
export default function FaultInjector({ placementId, props }: PlacementProps) {
  if (props.mode === "slow") {
    use(slow(placementId));
    return <div className="ref-card" data-fault="slow-done" />;
  }
  throw new Error("Reference fault injector");
}
