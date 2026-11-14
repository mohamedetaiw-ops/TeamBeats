"use client";

import { useId, useState, useSyncExternalStore } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import "@/components/experience/toolbox.css";
import { Widget } from "@/components/ui/Widget";

const noSubscription = () => () => undefined;

/** Offset of a zone from UTC at an instant, in milliseconds. */
function offset(zone: string, instant: number): number {
  const parts = Object.fromEntries(
    new Intl.DateTimeFormat("en-US", { timeZone: zone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" })
      .formatToParts(instant).filter((p) => p.type !== "literal").map((p) => [p.type, Number(p.value)]),
  );
  return Date.UTC(parts.year!, parts.month! - 1, parts.day!, parts.hour!, parts.minute!, parts.second!) - Math.floor(instant / 1000) * 1000;
}

/** The instant of a wall-clock time (`yyyy-mm-ddThh:mm`) in a zone; checked twice so daylight saving edges land right. */
export function instantOf(local: string, zone: string): number | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(local);
  if (!m) return null;
  const wall = Date.UTC(+m[1]!, +m[2]! - 1, +m[3]!, +m[4]!, +m[5]!);
  let instant = wall - offset(zone, wall);
  instant = wall - offset(zone, instant);
  return instant;
}

function zonesList(): string[] {
  const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf?.("timeZone") ?? [];
  return supported.includes("UTC") ? supported : ["UTC", ...supported];
}

/**
 * Tool box time zone converter (spec 008 FR-050): a date and time in one zone shown in another. Everything runs in the
 * browser with the platform time zone data; nothing is sent anywhere.
 */
export default function TimeConverter(_: PlacementProps) {
  const t = useTranslations("toolbox.converter");
  const locale = useLocale();
  const id = useId();
  const hydrated = useSyncExternalStore(noSubscription, () => true, () => false);
  const own = hydrated ? Intl.DateTimeFormat().resolvedOptions().timeZone : "UTC";
  const [when, setWhen] = useState("");
  const [from, setFrom] = useState<string | null>(null);
  const [to, setTo] = useState("UTC");
  const source = from ?? own;
  const zones = hydrated ? zonesList() : ["UTC"];
  const instant = when ? instantOf(when, source) : null;
  const result =
    instant === null ? null : new Intl.DateTimeFormat(locale, { timeZone: to, dateStyle: "full", timeStyle: "short" }).format(instant);
  return (
    <Widget title={t("title")}>
      <div className="toolbox-form">
        <label htmlFor={`${id}-when`}>{t("when")}</label>
        <input id={`${id}-when`} type="datetime-local" value={when} onChange={(e) => setWhen(e.target.value)} />
        <label htmlFor={`${id}-from`}>{t("from")}</label>
        <select id={`${id}-from`} value={source} onChange={(e) => setFrom(e.target.value)}>
          {zones.map((z) => (
            <option key={z} value={z}>
              {z}
            </option>
          ))}
        </select>
        <label htmlFor={`${id}-to`}>{t("to")}</label>
        <select id={`${id}-to`} value={to} onChange={(e) => setTo(e.target.value)}>
          {zones.map((z) => (
            <option key={z} value={z}>
              {z}
            </option>
          ))}
        </select>
      </div>
      <p className="toolbox-result" role="status" aria-live="polite">
        {result ? t("result", { time: result }) : t("empty")}
      </p>
    </Widget>
  );
}
