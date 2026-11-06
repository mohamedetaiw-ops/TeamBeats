import type { ComponentType } from "react";
import type { PlacementProps } from "./contract";

type Loader = () => Promise<{ default: ComponentType<PlacementProps> }>;

/**
 * Component registry of the web app: a component reference (`id@major`) to a lazy import of its code in
 * components/placements. Only this file names components; layouts, templates and pages never do (spec 032 FR-014).
 * Modules add their components here when they ship them. The reference packs exist for contract tests and are only
 * included when the build sets NEXT_PUBLIC_TB_REFERENCE_PACKS, so production bundles leave them out.
 */
const product: Record<string, Loader> = {};

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
