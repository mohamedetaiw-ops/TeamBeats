"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { AgendaRow, type LayerOption } from "@/components/ui/CalendarParts";
import { Button, ButtonLink } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { LAYER_LOOK, liveNow, touches, zoneDay, type CalendarItem, type Layer, type Rsvp, type SourceStatus } from "@/lib/calendar";
import "./calendar.css";

export type Load<T> = { state: "loading" } | { state: "failed"; messages: string[] } | { state: "ok"; data: T };

/** Reads one calendar endpoint in the browser, again whenever the URL or the attempt changes. */
export function useCalendar<T>(url: string | null, attempt = 0): Load<T> {
  const [result, setResult] = useState<{ key: string; load: Load<T> } | null>(null);
  const key = `${url}#${attempt}`;
  useEffect(() => {
    if (!url) return;
    let live = true;
    fetch(url, { cache: "no-store" })
      .then(async (response): Promise<Load<T>> => (response.ok ? { state: "ok", data: (await response.json()) as T } : { state: "failed", messages: await problemMessages(response) }))
      .catch((): Load<T> => ({ state: "failed", messages: [] }))
      .then((load) => {
        if (live) setResult({ key, load });
      });
    return () => {
      live = false;
    };
  }, [url, key]);
  return result?.key === key ? result.load : { state: "loading" };
}

/** The layer options for chips and legends, in display order, with their translated labels. */
export function layerOptions(t: (key: string) => string, layers: readonly Layer[]): LayerOption[] {
  return layers.map((key) => ({ key, label: t(`layers.${key}`), icon: LAYER_LOOK[key].icon, swatch: LAYER_LOOK[key].swatch }));
}

/** Items that touch a day, all-day first (the API orders them). */
export function itemsOn(items: readonly CalendarItem[], day: string, zone: string): CalendarItem[] {
  return items.filter((i) => touches(i, day, zone));
}

/** The freshness line (spec 037 FR-023): which source could not be read now, and since when its data is. */
export function Freshness({ sources, onRetry }: { sources: readonly SourceStatus[]; onRetry?: () => void }) {
  const t = useTranslations("calendar");
  const locale = useLocale();
  const stale = sources.filter((s) => s.state === "degraded");
  const setup = sources.filter((s) => s.state !== "ok" && s.state !== "degraded");
  if (stale.length === 0 && setup.length === 0) return null;
  const time = new Intl.DateTimeFormat(locale, { timeStyle: "short", dateStyle: "medium" });
  return (
    <div className="cal-fresh" role="status">
      {stale.map((s) => (
        <p key={s.source}>
          {s.asOf ? t("fresh.stale", { source: t(`sources.${source(s.source)}`), time: time.format(new Date(s.asOf)) }) : t("fresh.down", { source: t(`sources.${source(s.source)}`) })}
        </p>
      ))}
      {setup.map((s) => (
        <p key={s.source}>{t("fresh.setup", { source: t(`sources.${source(s.source)}`) })}</p>
      ))}
      {stale.length > 0 && onRetry ? (
        <Button size="xs" variant="quiet" onClick={onRetry}>
          {t("fresh.retry")}
        </Button>
      ) : null}
    </div>
  );
}

const known = ["outlook", "google", "events", "holidays", "leave"] as const;
const source = (s: string) => (known as readonly string[]).includes(s) ? s : "other";

/** One item as an agenda row with its details and actions (spec 036 DLS-071, spec 037 FR-008). */
export function ItemRow({
  item,
  zone,
  day,
  now,
  open,
  onToggle,
  onChanged,
}: {
  item: CalendarItem;
  zone: string;
  /** The day the row is listed under, so a multi-day item says it continues. */
  day?: string;
  now: number;
  open: boolean;
  onToggle: () => void;
  onChanged?: () => void;
}) {
  const t = useTranslations("calendar");
  const locale = useLocale();
  const clock = new Intl.DateTimeFormat(locale, { timeZone: zone, timeStyle: "short" });
  const long = new Intl.DateTimeFormat(locale, { timeZone: zone, dateStyle: "medium", timeStyle: "short" });
  const dates = new Intl.DateTimeFormat(locale, { timeZone: "UTC", day: "numeric", month: "short" });
  const look = LAYER_LOOK[item.layer];

  const title =
    item.layer === "team_leave"
      ? t("teamAway", { count: item.count ?? 0 })
      : item.title ?? (item.isPrivate ? t("private") : item.layer === "my_leave" ? t("onLeave") : t("busy"));

  let time: string;
  if (item.allDay) {
    const length = item.startDate && item.endDate ? (Date.parse(item.endDate) - Date.parse(item.startDate)) / 86_400_000 : 1;
    time = item.halfStart || item.halfEnd ? t("halfDay") : length > 1 ? t("dateRange", { start: dates.format(Date.parse(item.startDate!)), end: dates.format(Date.parse(item.endDate!) - 86_400_000) }) : t("allDay");
  } else {
    const startsEarlier = day && zoneDay(item.start!, zone) < day;
    time = startsEarlier ? t("until", { end: clock.format(new Date(item.end!)) }) : t("timeRange", { start: clock.format(new Date(item.start!)), end: clock.format(new Date(item.end!)) });
  }

  const status = item.status === "cancelled" ? "cancelled" : item.status === "pending" ? "pending" : item.status === "tentative" ? "tentative" : null;
  const statusLabel = status ? t(`status.${status}`) : item.free ? t("status.free") : null;
  const meta = item.location ?? (item.onlineUrl ? t("online") : t(`sources.${source(item.source)}`));
  const live = liveNow(item, now);
  const primary = live ? (
    <ButtonLink href={item.onlineUrl!} variant="primary" size="sm" target="_blank" rel="noopener noreferrer" aria-label={t("joinLabel", { title })}>
      {t("actions.join")}
    </ButtonLink>
  ) : null;

  return (
    <AgendaRow
      swatch={look.swatch}
      icon={look.icon}
      layerLabel={t(`layers.${item.layer}`)}
      time={time}
      title={title}
      meta={meta}
      status={status}
      statusLabel={statusLabel}
      primary={primary}
      open={open}
      onToggle={onToggle}
      details={
        <>
          {!item.allDay && item.start && item.end ? <p className="cal-detail-line">{t("timeRange", { start: long.format(new Date(item.start)), end: clock.format(new Date(item.end)) })}</p> : null}
          {item.recurring ? <p className="cal-detail-line">{t("recurring")}</p> : null}
          {item.location ? <p className="cal-detail-line">{item.location}</p> : null}
          {item.description ? <p className="cal-detail-text">{item.description}</p> : null}
          {item.rsvp ? <RsvpControl item={item} rsvp={item.rsvp} onChanged={onChanged} /> : null}
          <div className="cal-actions">
            {item.actions.includes("join") && item.onlineUrl && !live ? (
              <ButtonLink href={item.onlineUrl} size="sm" target="_blank" rel="noopener noreferrer" aria-label={t("joinLabel", { title })}>
                {t("actions.join")}
              </ButtonLink>
            ) : null}
            {item.actions.includes("open_source") && item.webLink ? (
              <ButtonLink href={item.webLink} size="sm" variant="quiet" iconEnd="external" target="_blank" rel="noopener noreferrer">
                {t(item.source === "google" ? "actions.openGoogle" : "actions.openOutlook")}
              </ButtonLink>
            ) : null}
            {item.actions.includes("open_leave") && item.leaveRequestId ? (
              <ButtonLink href={`/leave/requests/${item.leaveRequestId}`} size="sm" variant="quiet">
                {t("actions.openLeave")}
              </ButtonLink>
            ) : null}
            {item.actions.includes("view_team") ? (
              <ButtonLink href="/calendar?view=team" size="sm" variant="quiet">
                {t("actions.viewTeam")}
              </ButtonLink>
            ) : null}
          </div>
        </>
      }
    />
  );
}

/** Going, maybe or not going for a company event (spec 037 FR-018). Counts show only when the API gives them. */
function RsvpControl({ item, rsvp, onChanged }: { item: CalendarItem; rsvp: Rsvp; onChanged?: () => void }) {
  const t = useTranslations("calendar");
  const [answer, setAnswer] = useState(rsvp.answer);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const answers = ["going", "maybe", "declined"] as const;

  async function choose(value: (typeof answers)[number]) {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`/api/v1/calendar/events/${item.sourceId}/response`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ occurrenceKey: item.occurrenceKey, answer: value }),
      });
      if (response.ok) {
        setAnswer(value);
        onChanged?.();
      } else {
        setError((await problemMessages(response)).join(" ") || t("rsvp.failed"));
      }
    } catch {
      setError(t("rsvp.failed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="cal-rsvp">
      <p className="cal-detail-line">{rsvp.open ? t("rsvp.question") : t("rsvp.closed")}</p>
      {rsvp.open ? (
        <div className="cal-actions" role="group" aria-label={t("rsvp.question")}>
          {answers.map((a) => (
            <Button key={a} size="xs" variant={answer === a ? "primary" : "secondary"} aria-pressed={answer === a} disabled={busy} onClick={() => void choose(a)}>
              {t(`rsvp.${a}`)}
            </Button>
          ))}
        </div>
      ) : null}
      {rsvp.going !== null ? <p className="cal-detail-line">{t("rsvp.counts", { going: rsvp.going, maybe: rsvp.maybe ?? 0, declined: rsvp.declined ?? 0 })}</p> : null}
      {error ? (
        <p className="cal-error" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** A link to the full workspace at a view and date. */
export function WorkspaceLink({ view, date, children, className }: { view?: string; date?: string; children: React.ReactNode; className?: string }) {
  const query = new URLSearchParams();
  if (view) query.set("view", view);
  if (date) query.set("date", date);
  const href = `/calendar${query.size ? `?${query.toString()}` : ""}`;
  return (
    <Link href={href} className={className}>
      {children}
    </Link>
  );
}
