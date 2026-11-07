"use client";

import { type ReactNode, useEffect, useState, useSyncExternalStore } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import "@/components/experience/toolbox.css";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";

type Report = {
  place: { name: string; country: string | null };
  temperatureC: number;
  condition: string;
  highC: number | null;
  lowC: number | null;
};
type Answer = { state: "ok" | "not_found" | "degraded" | "unavailable"; asOf: string | null; report: Report | null };

const CONDITIONS = ["clear", "partly_cloudy", "cloudy", "fog", "drizzle", "rain", "snow", "thunderstorm", "unknown"] as const;

const never = () => () => {};
const ownZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;
const none = () => null;

/** The city part of an IANA zone, such as Dubai for Asia/Dubai; none for UTC and other zones without a city. */
function cityOf(zone: string | null): string {
  if (!zone || !zone.includes("/")) return "";
  return zone.split("/").pop()!.replaceAll("_", " ");
}

/**
 * Tool box weather (spec 008 FR-050): current weather and today's range for the configured place, or for the city of
 * the viewer's time zone when none is set. The platform's weather connector answers; only the place name is sent.
 */
export default function Weather({ props }: PlacementProps) {
  const t = useTranslations("toolbox.weather");
  const locale = useLocale();
  const zone = useSyncExternalStore(never, ownZone, none);
  const configured = typeof props.location === "string" ? props.location.trim() : "";
  const location = configured || cityOf(zone);
  // Each request has a key; an answer for another key means the current one is still loading.
  const [attempt, setAttempt] = useState(0);
  const [result, setResult] = useState<{ key: string; answer: Answer | "failed" } | null>(null);
  const key = `${location}#${attempt}`;

  useEffect(() => {
    if (!location) return;
    let live = true;
    const refresh = attempt > 0 ? "&refresh=true" : "";
    fetch(`/api/v1/weather?location=${encodeURIComponent(location)}${refresh}`)
      .then(async (response) => (response.ok ? ((await response.json()) as Answer) : "failed" as const))
      .catch(() => "failed" as const)
      .then((answer) => {
        if (live) setResult({ key, answer });
      });
    return () => {
      live = false;
    };
  }, [location, attempt, key]);

  const answer = result?.key === key ? result.answer : "loading";

  const shell = (body: ReactNode, busy = false) => (
    <section className="toolbox" aria-label={t("title")} aria-busy={busy || undefined}>
      <h2 className="toolbox-title">{t("title")}</h2>
      {body}
    </section>
  );

  if (zone === null && !configured) return shell(null, true);
  if (!location) return shell(<StateView kind="empty" title={t("noPlace")} message="" />);
  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="6rem" />, true);

  const retry = (
    <Button variant="secondary" onClick={() => setAttempt((n) => n + 1)}>
      {t("retry")}
    </Button>
  );
  if (answer === "failed" || answer.state === "unavailable" || (!answer.report && answer.state !== "not_found")) {
    return shell(<StateView kind="error" title={t("unavailable")} message="" action={retry} />);
  }
  if (answer.state === "not_found" || !answer.report) {
    return shell(<StateView kind="empty" title={t("notFound", { place: location })} message="" />);
  }

  const r = answer.report;
  const degrees = new Intl.NumberFormat(locale, { style: "unit", unit: "celsius", maximumFractionDigits: 0 });
  const condition = (CONDITIONS as readonly string[]).includes(r.condition) ? r.condition : "unknown";
  const time = answer.asOf ? new Intl.DateTimeFormat(locale, { timeStyle: "short" }).format(new Date(answer.asOf)) : "";
  return shell(
    <div className="toolbox-weather">
      <p className="toolbox-weather-place">
        <bdi>{r.place.country ? t("place", { name: r.place.name, country: r.place.country }) : r.place.name}</bdi>
      </p>
      <p className="toolbox-weather-now">
        <span className="toolbox-weather-temperature">{degrees.format(r.temperatureC)}</span>
        <span>{t(`condition.${condition as (typeof CONDITIONS)[number]}`)}</span>
      </p>
      {r.highC !== null && r.lowC !== null ? <p className="toolbox-weather-range">{t("range", { high: degrees.format(r.highC), low: degrees.format(r.lowC) })}</p> : null}
      {answer.state === "degraded" ? (
        <div role="status" className="toolbox-weather-stale">
          <p>{t("stale", { time })}</p>
          {retry}
        </div>
      ) : null}
    </div>,
  );
}
