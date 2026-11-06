"use client";

import { Component, lazy, Suspense, useEffect, useSyncExternalStore, type ComponentType, type CSSProperties, type LazyExoticComponent, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { usePlacementEvents } from "@/lib/experience/events";
import { loaderFor } from "@/lib/experience/registry";
import { reportPlacementFailure } from "@/lib/experience/telemetry";
import type { RenderedPlacement } from "@/lib/experience/types";
import { StateView } from "@/components/ui/StateView";

const noSubscription = () => () => undefined;

const lazyCache = new Map<string, LazyExoticComponent<ComponentType<PlacementProps>>>();

function lazyComponent(componentRef: string): LazyExoticComponent<ComponentType<PlacementProps>> | null {
  const loader = loaderFor(componentRef);
  if (!loader) {
    return null;
  }
  let component = lazyCache.get(componentRef);
  if (!component) {
    component = lazy(loader);
    lazyCache.set(componentRef, component);
  }
  return component;
}

type BoundaryProps = { placement: RenderedPlacement; fallback: ReactNode; children: ReactNode };

/** A failing placement shows its own error state; the rest of the page keeps working (spec 032 FR-066). */
class PlacementErrorBoundary extends Component<BoundaryProps, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch() {
    reportPlacementFailure({ componentRef: this.props.placement.componentRef, placementId: this.props.placement.id, kind: "error" });
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

const stateKinds = { loading: "loading", failed: "error", unavailable: "unsupported" } as const;

function PlacementState({ placement, kind }: { placement: RenderedPlacement; kind: "unavailable" | "failed" | "loading" }) {
  const t = useTranslations("experience.placement");
  return (
    <StateView kind={stateKinds[kind]} title={t(kind)} message="" data-placement-state={kind} data-placement-id={placement.id} />
  );
}

function Mounted({ placement, Loaded }: { placement: RenderedPlacement; Loaded: ComponentType<PlacementProps> }) {
  const locale = useLocale();
  const events = usePlacementEvents(placement.id, placement.componentRef, placement.publishes, placement.subscribes);
  // True once this placement's code has loaded and hydrated, so its events are wired.
  const ready = useSyncExternalStore(noSubscription, () => true, () => false);
  return (
    <div className="xp-channel" data-placement-ready={ready}>
      <Loaded placementId={placement.id} props={placement.props} context={{ locale, ...placement.context }} events={events} />
    </div>
  );
}

/**
 * Placement decorator (spec 032 plan, frontend): error boundary, size-reserving placeholder while code loads, the
 * unavailable state for components the API marked so or whose code is missing, and failure telemetry.
 */
export function Placement({ placement }: { placement: RenderedPlacement }) {
  const Loaded = placement.state === "ready" ? lazyComponent(placement.componentRef) : null;
  const missing = placement.state === "ready" && !Loaded;
  useEffect(() => {
    if (missing) {
      reportPlacementFailure({ componentRef: placement.componentRef, placementId: placement.id, kind: "missing" });
    }
  }, [missing, placement.componentRef, placement.id]);
  return (
    <div className="xp-placement" style={{ "--xp-span": placement.span } as CSSProperties} data-placement-id={placement.id}
      data-component={placement.componentRef}>
      {Loaded ? (
        <PlacementErrorBoundary placement={placement} fallback={<PlacementState placement={placement} kind="failed" />}>
          <Suspense fallback={<PlacementState placement={placement} kind="loading" />}>
            <Mounted placement={placement} Loaded={Loaded} />
          </Suspense>
        </PlacementErrorBoundary>
      ) : (
        <PlacementState placement={placement} kind="unavailable" />
      )}
    </div>
  );
}
