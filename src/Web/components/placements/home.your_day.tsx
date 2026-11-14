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
import { Badge } from "@/components/ui/Badge";
import { StatCards } from "@/components/ui/Blocks";
import { ButtonLink } from "@/components/ui/Button";
import { AgendaRow } from "@/components/ui/CalendarParts";
import { Widget } from "@/components/ui/Widget";
import { LAYER_LOOK } from "@/lib/calendar";
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
 * Your day (spec 008 FR-057, docs/design-system/components.md): three tinted stat cards (spec 036 DLS-039) (tasks to approve, overdue tasks
 * and meetings left today) and today's agenda from the viewer's own calendar (spec 018), each meeting with its time
 * and a "starts in" badge for the next hour, drawn with the calendar's own agenda row (spec 036 DLS-090) in the shared
 * widget frame (DLS-089). Both sources are read with the viewer's session; when the calendar is not
 * connected the agenda becomes one line with the connect link, and the meetings tile is left out, never guessed.
 */
export default function YourDay({ props }: PlacementProps) {
  const t = useTranslations("home.day");
  const tc = useTranslations("calendar");
  const [open, setOpen] = useState<string | null>(null);
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
    <Widget title={title} busy={busy} testId="home-your-day" action={<Link href="/tasks">{t("inbox")}</Link>}>
      {body}
    </Widget>
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
      <StatCards
        label={t("counts")}
        dense
        stats={[
          ...(sources.tasks
            ? [
                { key: "approve", label: t("toApprove"), value: format.number(tasks.length), icon: "inbox" as const, tone: "warm" as const, href: "/tasks" },
                { key: "overdue", label: t("overdue"), value: format.number(overdue), icon: "alert" as const, tone: "cool" as const, href: "/tasks" },
              ]
            : []),
          ...(connected
            ? [{ key: "meetings", label: t("meetings", { count: meetings.length }), value: format.number(meetings.length), icon: "calendar" as const, tone: "fresh" as const, href: "/calendar" }]
            : []),
        ]}
      />
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
        <ul className="ui-cal-agenda" aria-label={t("agenda")}>
          {meetings.slice(0, AGENDA).map((m) => {
            const starts = Date.parse(m.start);
            const soon = starts > now && starts - now <= SOON;
            const subject = m.isPrivate || !m.subject ? t("privateMeeting") : m.subject;
            return (
              <AgendaRow
                key={m.id}
                swatch={LAYER_LOOK.meeting.swatch}
                icon={LAYER_LOOK.meeting.icon}
                layerLabel={tc("layers.meeting")}
                time={tc("timeRange", { start: clock.format(new Date(m.start)), end: clock.format(new Date(m.end)) })}
                title={subject}
                meta={m.location}
                primary={
                  soon ? (
                    <Badge tone="warning">{t("startsIn", { minutes: Math.max(1, Math.round((starts - now) / 60_000)) })}</Badge>
                  ) : starts <= now ? (
                    <Badge tone="warning">{t("now")}</Badge>
                  ) : null
                }
                open={open === m.id}
                onToggle={() => setOpen(open === m.id ? null : m.id)}
                details={
                  <div className="ui-actions">
                    <ButtonLink href={m.webLink} size="sm" variant="quiet" iconEnd="external" target="_blank" rel="noopener noreferrer">
                      {tc("actions.openOutlook")}
                    </ButtonLink>
                  </div>
                }
              />
            );
          })}
        </ul>
      )}
    </>,
  );
}
