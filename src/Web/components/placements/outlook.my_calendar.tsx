"use client";

import type { ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { OutlookStateView, StaleNote, useHydrated, useNow, useOutlookData, useReturnPath, type OutlookState } from "@/components/experience/outlook";
import { StateView } from "@/components/ui/StateView";

type Item = {
  id: string;
  source: string;
  subject: string | null;
  start: string;
  end: string;
  isAllDay: boolean;
  isPrivate: boolean;
  free: boolean;
  tentative: boolean;
  location: string | null;
  onlineJoinUrl: string | null;
  webLink: string;
  halfDay?: boolean;
};
type Answer = { state: OutlookState; asOf: string | null; timeZone: string; from: string; to: string; items: Item[] };

const JOIN_LEAD_MS = 10 * 60_000;

/** The calendar day of an item in the zone, as yyyy-mm-dd; all-day items keep the date Microsoft gives. */
function dayOf(item: Item, zone: string): string {
  if (item.isAllDay) return item.start.slice(0, 10);
  return new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date(item.start));
}

/**
 * My calendar (spec 018 US3): the employee's own events for the coming days in the tenant time zone, grouped by day
 * with all-day items first (the server orders them). Free and tentative are text markers; Join is the primary action
 * from 10 minutes before an online meeting starts until it ends. Every Outlook event opens in Outlook on the web; holidays from the working calendar show without a link.
 */
export default function MyCalendar({ props }: PlacementProps) {
  const t = useTranslations("outlook.calendar");
  const locale = useLocale();
  const returnTo = useReturnPath();
  const hydrated = useHydrated();
  const now = useNow();
  const days = typeof props.days === "number" && props.days >= 1 && props.days <= 14 ? Math.floor(props.days) : 7;
  const hidePrivate = props.hidePrivate === true;
  const { answer, retry } = useOutlookData<Answer>(`/api/v1/me/calendar?days=${days}&hidePrivate=${hidePrivate}`);

  const shell = (body: ReactNode, busy = false) => (
    <section className="outlook" aria-label={t("title")} aria-busy={busy || undefined}>
      <div className="outlook-header">
        <h2 className="outlook-title">{t("title")}</h2>
      </div>
      {body}
    </section>
  );

  if (answer === "loading" || !hydrated || now === null) return shell(<StateView kind="loading" minBlockSize="10rem" />, true);
  const state = answer === "failed" || answer === "denied" ? answer : answer.state;
  if (answer === "failed" || answer === "denied" || (state !== "ok" && state !== "degraded")) {
    return shell(<OutlookStateView state={state} returnTo={returnTo} retry={retry} noun="calendar" />);
  }

  const zone = answer.timeZone || "UTC";
  const clock = new Intl.DateTimeFormat(locale, { timeZone: zone, timeStyle: "short" });
  const heading = new Intl.DateTimeFormat(locale, { timeZone: "UTC", weekday: "long", day: "numeric", month: "long" });
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date(now));
  const tomorrow = new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date(now + 86_400_000));

  const groups = new Map<string, Item[]>();
  for (const item of answer.items) {
    const day = dayOf(item, zone);
    if (!groups.has(day)) groups.set(day, []);
    groups.get(day)!.push(item);
  }
  const dayTitle = (day: string) => {
    if (day === today) return t("today");
    if (day === tomorrow) return t("tomorrow");
    return heading.format(new Date(`${day}T12:00:00Z`));
  };

  return shell(
    <>
      {answer.state === "degraded" ? <StaleNote asOf={answer.asOf} locale={locale} retry={retry} /> : null}
      {answer.items.length === 0 ? (
        <StateView kind="empty" title={t("empty")} message="" />
      ) : (
        [...groups.entries()].map(([day, items]) => (
          <div key={day} className="outlook-day">
            <h3 className="outlook-day-title">{dayTitle(day)}</h3>
            <ul className="outlook-list">
              {items.map((item) => {
                if (item.source !== "outlook") {
                  // A TeamBeats item (FR-017): a holiday of the person's working calendar, labelled by its source.
                  return (
                    <li key={item.id} className="outlook-item">
                      <span className="outlook-line">
                        <span className="outlook-time">{t("allDay")}</span>
                        <span className="outlook-markers">{t("sources.holiday")}</span>
                      </span>
                      <bdi className="outlook-subject">{item.halfDay ? t("halfHoliday") : t("holiday")}</bdi>
                    </li>
                  );
                }
                const start = new Date(item.start).getTime();
                const end = new Date(item.end).getTime();
                const joinNow = item.onlineJoinUrl !== null && now >= start - JOIN_LEAD_MS && now < end;
                const when = item.isAllDay ? t("allDay") : t("range", { start: clock.format(start), end: clock.format(end) });
                const subject = item.subject ?? t("private");
                const markers = [item.tentative ? t("markers.tentative") : null, item.free ? t("markers.free") : null].filter(Boolean);
                const where = item.location || (item.onlineJoinUrl ? t("online") : null);
                return (
                  <li key={item.id} className="outlook-item">
                    <a className="outlook-link" href={item.webLink} target="_blank" rel="noopener noreferrer">
                      <span className="outlook-line">
                        <span className="outlook-time">{when}</span>
                        {markers.length > 0 ? <span className="outlook-markers">{markers.join(t("markerSeparator"))}</span> : null}
                      </span>
                      <bdi className="outlook-subject">{subject}</bdi>
                      {where ? <bdi className="outlook-preview">{where}</bdi> : null}
                      <span className="visually-hidden">{t("opensOutlook")}</span>
                    </a>
                    {item.onlineJoinUrl ? (
                      <a
                        className={`button ${joinNow ? "button-primary" : "button-quiet"} outlook-join`}
                        href={item.onlineJoinUrl}
                        target="_blank"
                        rel="noopener noreferrer"
                        aria-label={t("joinLabel", { subject })}
                      >
                        {t("join")}
                      </a>
                    ) : null}
                  </li>
                );
              })}
            </ul>
          </div>
        ))
      )}
    </>,
  );
}
