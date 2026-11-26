"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { StatusDot, type StatusTone } from "@/components/ui/Status";

type Presence = { state: string; availability: string | null; activity: string | null };

const TONE: Record<string, StatusTone> = { available: "success", away: "warning", busy: "danger", do_not_disturb: "danger", offline: "neutral" };

/**
 * A colleague's Teams presence on their profile (spec 018 R3), read in the browser so a slow Teams answer never holds
 * the page. It shows only when the API gives a known availability; any other answer leaves the line out.
 */
export function PresenceStatus({ personId }: { personId: string }) {
  const t = useTranslations("people");
  const [presence, setPresence] = useState<Presence | null>(null);
  useEffect(() => {
    let live = true;
    fetch(`/api/v1/people/${encodeURIComponent(personId)}/presence`, { cache: "no-store" })
      .then(async (response) => (response.ok ? ((await response.json()) as Presence) : null))
      .catch(() => null)
      .then((value) => {
        if (live) setPresence(value);
      });
    return () => {
      live = false;
    };
  }, [personId]);

  if (!presence || presence.state !== "ok" || !presence.availability || presence.availability === "unknown") return null;
  return (
    <span className="people-presence" data-testid="person-presence">
      <span className="visually-hidden">{t("presence.label")} </span>
      <StatusDot tone={TONE[presence.availability] ?? "neutral"}>
        {t(`presence.${presence.availability}` as "presence.available")}
        {presence.activity ? `${t("separator")}${t(`presence.${presence.activity}` as "presence.available")}` : null}
      </StatusDot>
    </span>
  );
}
