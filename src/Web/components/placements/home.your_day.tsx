"use client";

import Link from "next/link";
import { useEffect, useState, type ReactNode } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import type { InboxItem } from "@/lib/services/types";
import { useNow } from "@/lib/clock";
import { StateView } from "@/components/ui/StateView";
import { connectHref } from "@/components/experience/outlook";
import { Icon } from "@/components/ui/Icon";
import "@/features/services/home.css";

type Meeting = { id: string; subject: string | null; start: string; end: string; isAllDay: boolean; isPrivate: boolean; location: string | null; webLink: string; source: string };
type Calendar = { state: string; timeZone: string; items: Meeting[] };
type Sources = { tasks: InboxItem[] | null; calendar: Calendar | null };

const SOON = 60 * 60_000;
const AGENDA = 4;

async function read<T>(url: string): Promise<T | null> {
  try {
    const response = await fetch(url, { cache: "no-store" });
    return response.ok ? ((await response.json()) as T) : null;
  } catch {
    return null;
  }
}

/**
 * Your day (spec 008 FR-057, docs/design-system/components.md): three tinted counts (tasks to approve, overdue tasks
 * and meetings left today) and today's agenda from the viewer's own calendar (spec 018), each meeting with its time
 * and a "starts in" badge for the next hour. Both sources are read with the viewer's session; when the calendar is not
 * connected the agenda becomes one line with the connect link, and the meetings tile is left out, never guessed.
 */
export default function YourDay({ props }: PlacementProps) {
  const t = useTranslations("home.day");
  const locale = useLocale();
  const format = useFormatter();
  const now = useNow();
  const title = localized(props.title, locale) || t("title");
  const [sources, setSources] = useState<Sources | "loading">("loading");

  useEffect(() => {
    let live = true;
    void Promise.all([read<InboxItem[]>("/api/v1/tasks"), read<Calendar>("/api/v1/me/calendar?days=1&hidePrivate=false")]).then(([tasks, calendar]) => {
      if (live) setSources({ tasks, calendar });
    });
    return () => {
      live = false;
    };
  }, []);

  const shell = (body: ReactNode, busy = false) => (
    <section className="home-card" aria-labelledby="home-day-title" aria-busy={busy || undefined} data-testid="home-your-day">
      <div className="home-card-head">
        <h2 id="home-day-title">{title}</h2>
        <Link href="/tasks">{t("inbox")}</Link>
      </div>
      {body}
    </section>
  );

  if (sources === "loading" || now === null) return shell(<StateView kind="loading" minBlockSize="10rem" />, true);

  const tasks = sources.tasks ?? [];
  const overdue = tasks.filter((i) => i.dueAt && Date.parse(i.dueAt) < now).length;
  const connected = sources.calendar?.state === "ok" || sources.calendar?.state === "degraded";
  const zone = sources.calendar?.timeZone || Intl.DateTimeFormat().resolvedOptions().timeZone;
  const clock = new Intl.DateTimeFormat(locale, { timeZone: zone, timeStyle: "short" });
  const meetings = connected
    ? sources.calendar!.items
        .filter((m) => !m.isAllDay && m.source === "outlook" && Date.parse(m.end) >= now)
        .sort((a, b) => Date.parse(a.start) - Date.parse(b.start))
    : [];

  return shell(
    <>
      <ul className="home-stats">
        {sources.tasks ? (
          <>
            <li data-tone="warm">
              <strong>{format.number(tasks.length)}</strong>
              <span>{t("toApprove")}</span>
            </li>
            <li data-tone="accent">
              <strong>{format.number(overdue)}</strong>
              <span>{t("overdue")}</span>
            </li>
          </>
        ) : null}
        {connected ? (
          <li data-tone="fresh">
            <strong>{format.number(meetings.length)}</strong>
            <span>{t("meetings", { count: meetings.length })}</span>
          </li>
        ) : null}
      </ul>
      {!connected ? (
        sources.calendar?.state === "not_connected" || sources.calendar?.state === "reconnect" ? (
          <div className="home-connect">
            <span className="ui-icon-tile" aria-hidden="true">
              <Icon name="calendar" size={18} />
            </span>
            <p className="home-meta">{t("noCalendar")}</p>
            <a className="button button-secondary" href={connectHref("/")}>
              {t("connect")}
            </a>
          </div>
        ) : (
          <p className="home-meta">{t("calendarUnavailable")}</p>
        )
      ) : meetings.length === 0 ? (
        <p className="home-meta">{t("noMeetings")}</p>
      ) : (
        <ol className="home-agenda" aria-label={t("agenda")}>
          {meetings.slice(0, AGENDA).map((m) => {
            const starts = Date.parse(m.start);
            const soon = starts > now && starts - now <= SOON;
            return (
              <li key={m.id} className="home-agenda-item">
                <span className="home-agenda-time">{clock.format(new Date(m.start))}</span>
                <span className="home-agenda-text">
                  <a href={m.webLink} target="_blank" rel="noopener noreferrer">
                    <bdi>{m.isPrivate || !m.subject ? t("privateMeeting") : m.subject}</bdi>
                  </a>
                  {m.location ? (
                    <span className="home-meta">
                      <bdi>{m.location}</bdi>
                    </span>
                  ) : null}
                </span>
                {soon ? <span className="home-agenda-soon">{t("startsIn", { minutes: Math.max(1, Math.round((starts - now) / 60_000)) })}</span> : starts <= now ? <span className="home-agenda-soon">{t("now")}</span> : null}
              </li>
            );
          })}
        </ol>
      )}
    </>,
  );
}
