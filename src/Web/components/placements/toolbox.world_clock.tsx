"use client";

import { useSyncExternalStore } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import "@/components/experience/toolbox.css";
import { Widget } from "@/components/ui/Widget";

/** Minute-aligned now, so every clock on the page ticks together. Null on the server, so the markup never mismatches. */
function subscribe(tick: () => void) {
  const timer = setInterval(tick, 15000);
  return () => clearInterval(timer);
}
const minute = () => Math.floor(Date.now() / 60000) * 60000;
const none = () => null;

function valid(zone: string): boolean {
  try {
    new Intl.DateTimeFormat("en", { timeZone: zone });
    return true;
  } catch {
    return false;
  }
}

/**
 * Tool box world clock (spec 008 FR-050). Shows the current time in the configured IANA zones, or in the viewer's own
 * zone and UTC when none are set. Names and digits follow the page language; a zone the browser does not know is left out.
 */
export default function WorldClock({ props }: PlacementProps) {
  const t = useTranslations("toolbox.clock");
  const locale = useLocale();
  const now = useSyncExternalStore(subscribe, minute, none);
  const configured = Array.isArray(props.zones) ? props.zones.filter((z): z is string => typeof z === "string" && valid(z)) : [];
  if (now === null) {
    return (
      <Widget title={t("title")} busy />
    );
  }
  const own = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const zones = configured.length > 0 ? configured : [...new Set([own, "UTC"])];
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: own, dateStyle: "short" }).format(now);
  return (
    <Widget title={t("title")}>
      <ul className="toolbox-clocks">
        {zones.map((zone) => {
          const name = new Intl.DateTimeFormat(locale, { timeZone: zone, timeZoneName: "longGeneric" })
            .formatToParts(now).find((p) => p.type === "timeZoneName")?.value ?? zone;
          const sameDay = new Intl.DateTimeFormat("en-CA", { timeZone: zone, dateStyle: "short" }).format(now) === today;
          const time = new Intl.DateTimeFormat(locale, { timeZone: zone, hour: "numeric", minute: "2-digit", weekday: sameDay ? undefined : "short" }).format(now);
          return (
            <li key={zone} className="toolbox-clock">
              <span className="toolbox-clock-time">
                <time dateTime={new Date(now).toISOString()}>{time}</time>
              </span>
              <span className="toolbox-clock-zone">
                <bdi>{name}</bdi>
              </span>
            </li>
          );
        })}
      </ul>
    </Widget>
  );
}
