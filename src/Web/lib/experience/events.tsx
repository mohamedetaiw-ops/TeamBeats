"use client";

import { createContext, useContext, useMemo, useState, useSyncExternalStore, type ReactNode } from "react";
import type { PlacementEvents } from "./contract";
import { reportPlacementFailure } from "./telemetry";
import type { JsonValue } from "./types";

type Handler = (payload: Record<string, JsonValue>) => void;
type Bus = Map<string, Set<Handler>>;

const BusContext = createContext<Bus | null>(null);

/** Largest event payload, serialized. Events carry filters and selections, not documents. */
export const maxPayloadBytes = 4096;

const noSubscription = () => () => undefined;

/** One channel per page: placements on other pages never hear each other. */
export function EventChannel({ children }: { children: ReactNode }) {
  const [bus] = useState<Bus>(() => new Map());
  // Marks the page as interactive once hydrated, so tests and the builder preview know events are wired.
  const ready = useSyncExternalStore(noSubscription, () => true, () => false);
  return (
    <BusContext.Provider value={bus}>
      <div className="xp-channel" data-experience-ready={ready}>
        {children}
      </div>
    </BusContext.Provider>
  );
}

function validPayload(payload: unknown): payload is Record<string, JsonValue> {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    return false;
  }
  try {
    return new TextEncoder().encode(JSON.stringify(payload)).length <= maxPayloadBytes;
  } catch {
    return false;
  }
}

/**
 * The channel a placement sees: it can publish only the events its manifest declares and hear only the ones it
 * subscribes to. Anything else is dropped and reported, so an undeclared coupling never works by accident.
 */
export function usePlacementEvents(placementId: string, componentRef: string, publishes: string[], subscribes: string[]): PlacementEvents {
  const bus = useContext(BusContext);
  return useMemo<PlacementEvents>(() => {
    const allowedOut = new Set(publishes);
    const allowedIn = new Set(subscribes);
    return {
      publish(event, payload) {
        if (!bus || !allowedOut.has(event)) {
          reportPlacementFailure({ componentRef, placementId, kind: "undeclared_event" });
          return;
        }
        if (!validPayload(payload)) {
          reportPlacementFailure({ componentRef, placementId, kind: "invalid_payload" });
          return;
        }
        // A copy, so a subscriber cannot change what the publisher holds.
        const copy = JSON.parse(JSON.stringify(payload)) as Record<string, JsonValue>;
        bus.get(event)?.forEach((handler) => handler(copy));
      },
      subscribe(event, handler) {
        if (!bus || !allowedIn.has(event)) {
          reportPlacementFailure({ componentRef, placementId, kind: "undeclared_event" });
          return () => undefined;
        }
        const handlers = bus.get(event) ?? new Set<Handler>();
        handlers.add(handler);
        bus.set(event, handlers);
        return () => handlers.delete(handler);
      },
    };
  }, [bus, placementId, componentRef, publishes, subscribes]);
}
