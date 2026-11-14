"use client";

import type { PlacementProps } from "@/lib/experience/contract";
import { CalendarCard } from "@/features/calendar/CalendarCard";

/**
 * My calendar (spec 018 US3, FR-017; spec 032 FR-070): from R2 the unified calendar card of spec 037 replaces the R1
 * list, so an existing My calendar placement renders that card. Outlook events arrive through the unified calendar,
 * merged with company events, holidays and leave and labelled by source; Join, Free, Tentative and private subjects
 * come from the card's agenda rows. The card shows one day at a time, so `days` no longer applies; the list is capped
 * at the card's default (spec 036 DLS-090).
 */
export default function MyCalendar(_: PlacementProps) {
  return <CalendarCard />;
}
