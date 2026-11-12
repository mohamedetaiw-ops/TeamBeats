"use client";

import type { PlacementProps } from "@/lib/experience/contract";
import { CalendarCard } from "@/features/calendar/CalendarCard";

/** The home calendar card (spec 037 FR-007, spec 008 FR-051): the week, the day's next items and alerts. */
export default function CalendarOverview({ props }: PlacementProps) {
  const limit = typeof props.items === "number" && props.items >= 1 && props.items <= 5 ? Math.floor(props.items) : 3;
  return <CalendarCard limit={limit} />;
}
