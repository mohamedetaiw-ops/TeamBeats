import type { ComponentType } from "react";
import type { PlacementProps } from "./contract";

type Loader = () => Promise<{ default: ComponentType<PlacementProps> }>;

/**
 * Component registry of the web app: a component reference (`id@major`) to a lazy import of its code in
 * components/placements. Only this file names components; layouts, templates and pages never do (spec 032 FR-014).
 * Modules add their components here when they ship them. The reference packs exist for contract tests and are only
 * included when the build sets NEXT_PUBLIC_TB_REFERENCE_PACKS, so production bundles leave them out.
 */
const product: Record<string, Loader> = {
  // The home briefing (spec 008 FR-056).
  "home.briefing@1": () => import("@/components/placements/home.briefing"),
  "home.your_day@1": () => import("@/components/placements/home.your_day"),
  // Tool box utilities (spec 008 FR-050).
  "toolbox.world_clock@1": () => import("@/components/placements/toolbox.world_clock"),
  "toolbox.time_converter@1": () => import("@/components/placements/toolbox.time_converter"),
  "toolbox.weather@1": () => import("@/components/placements/toolbox.weather"),
  // Microsoft 365 mail and calendar (spec 018 US2, US3).
  "outlook.my_mail@1": () => import("@/components/placements/outlook.my_mail"),
  "outlook.my_calendar@1": () => import("@/components/placements/outlook.my_calendar"),
  // The unified calendar card (spec 037 FR-007).
  "calendar.overview@1": () => import("@/components/placements/calendar.overview"),
  // People directory (spec 010 US4).
  "people.list@1": () => import("@/components/placements/people.list"),
  // Content and news (spec 009 FR-030, US8).
  "content.news_feed@1": () => import("@/components/placements/content.news_feed"),
  "content.rich_text@1": () => import("@/components/placements/content.rich_text"),
  "content.links@1": () => import("@/components/placements/content.links"),
  "content.call_to_action@1": () => import("@/components/placements/content.call_to_action"),
  "content.announcement_banner@1": () => import("@/components/placements/content.announcement_banner"),
  // Documents (spec 011 plan, pages).
  "documents.recent@1": () => import("@/components/placements/documents.recent"),
  // Services and the task inbox (spec 016, home components of spec 008).
  "services.my_services@1": () => import("@/components/placements/services.my_services"),
  "services.task_summary@1": () => import("@/components/placements/services.task_summary"),
};

const reference: Record<string, Loader> =
  process.env.NEXT_PUBLIC_TB_REFERENCE_PACKS === "true"
    ? {
        "reference.alpha.text@1": () => import("@/components/placements/reference.alpha.text"),
        "reference.alpha.links@1": () => import("@/components/placements/reference.alpha.links"),
        "reference.alpha.kpi@1": () => import("@/components/placements/reference.alpha.kpi"),
        "reference.alpha.filter@1": () => import("@/components/placements/reference.alpha.filter"),
        "reference.alpha.fault@1": () => import("@/components/placements/reference.alpha.fault"),
        "reference.beta.banner@1": () => import("@/components/placements/reference.beta.banner"),
        "reference.beta.list@1": () => import("@/components/placements/reference.beta.list"),
        "reference.beta.counter@1": () => import("@/components/placements/reference.beta.counter"),
        "reference.beta.restricted@1": () => import("@/components/placements/reference.beta.restricted"),
        "reference.beta.old@1": () => import("@/components/placements/reference.beta.old"),
      }
    : {};

const loaders: Record<string, Loader> = { ...product, ...reference };

export function loaderFor(componentRef: string): Loader | undefined {
  return Object.hasOwn(loaders, componentRef) ? loaders[componentRef] : undefined;
}
