"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { CalendarHeader, LayerChips, WeekStrip } from "@/components/ui/CalendarParts";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { addDays, firstWeekday, fromIso } from "@/components/ui/dates";
import { useHydrated, useNow } from "@/lib/clock";
import { LAYER_LOOK, LAYERS, daysFrom, weekOf, type EventsAnswer, type SummaryAnswer } from "@/lib/calendar";
import { Freshness, ItemRow, WorkspaceLink, itemsOn, layerOptions, useCalendar } from "./shared";

const VIEW_LINKS = ["agenda", "week", "month"] as const;

/**
 * The home calendar card (spec 037 FR-007, US1): light by design. The week with layer markers, the chosen day's next
 * items (at most `limit`), holiday and team alerts, and links into the workspace for anything more. The API decides
 * the layers, the items and who is a manager; the card only draws them.
 */
export function CalendarCard({ limit = 3 }: { limit?: number }) {
  const t = useTranslations("calendar");
  const locale = useLocale();
  const hydrated = useHydrated();
  const now = useNow();
  const weekStart = firstWeekday(locale);
  const [picked, setPicked] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [open, setOpen] = useState<string | null>(null);

  // The first answer gives the tenant's today; until then the browser's own day frames the request.
  const guess = hydrated && now !== null ? new Date(now).toISOString().slice(0, 10) : null;
  const anchor = picked ?? guess;
  const start = anchor ? weekOf(anchor, weekStart) : null;
  const summary = useCalendar<SummaryAnswer>(start ? `/api/v1/calendar/summary?from=${start}&to=${addDays(start, 7)}` : null, attempt);
  const today = summary.state === "ok" ? summary.data.today : null;
  const day = picked ?? today;
  const events = useCalendar<EventsAnswer>(day ? `/api/v1/calendar/events?from=${day}&to=${addDays(day, 1)}` : null, attempt);

  const shell = (body: React.ReactNode, busy = false) => (
    <section className="cal-card" aria-label={t("title")} aria-busy={busy || undefined}>
      {body}
    </section>
  );

  if (!start || summary.state === "loading" || now === null) return shell(<StateView kind="loading" minBlockSize="12rem" />, true);
  if (summary.state === "failed") return shell(<StateView kind="error" message={summary.messages.join(" ") || t("failed")} />);

  const data = summary.data;
  const selected = day ?? data.today;
  const zone = data.timeZone;
  const week = daysFrom(weekOf(selected, weekStart), 7);
  const enabled = LAYERS.filter((l) => data.enabled.includes(l));
  const legend = layerOptions(t, enabled);
  const period = new Intl.DateTimeFormat(locale, { month: "long", year: "numeric", timeZone: "UTC" }).format(fromIso(selected));
  const dayTitle =
    selected === data.today
      ? t("today")
      : selected === addDays(data.today, 1)
        ? t("tomorrow")
        : new Intl.DateTimeFormat(locale, { weekday: "long", day: "numeric", month: "long", timeZone: "UTC" }).format(fromIso(selected));
  const strip = week.map((date) => {
    const counts = data.days.find((d) => d.date === date)?.layers ?? {};
    const markers = LAYERS.filter((l) => (counts[l] ?? 0) > 0).map((l) => LAYER_LOOK[l].swatch);
    const count = Object.values(counts).reduce((a, b) => a + (b ?? 0), 0);
    return { date, markers, count };
  });

  const list = events.state === "ok" ? itemsOn(events.data.items, selected, zone) : [];
  const upcoming = selected === data.today ? list.filter((i) => i.allDay || i.status === "cancelled" || Date.parse(i.end!) > now) : list;
  const shown = upcoming.slice(0, limit);
  const more = upcoming.length - shown.length;

  return shell(
    <>
      <CalendarHeader
        title={t("title")}
        period={period}
        previousLabel={t("previousWeek")}
        nextLabel={t("nextWeek")}
        todayLabel={t("todayButton")}
        onPrevious={() => setPicked(addDays(selected, -7))}
        onNext={() => setPicked(addDays(selected, 7))}
        onToday={() => setPicked(null)}
      >
        <nav className="cal-card-views" aria-label={t("views.label")}>
          {VIEW_LINKS.map((v) => (
            <WorkspaceLink key={v} view={v} date={selected} className="cal-card-view">
              {t(`views.${v}`)}
            </WorkspaceLink>
          ))}
        </nav>
      </CalendarHeader>
      <WeekStrip label={t("weekLabel")} days={strip} selected={selected} today={data.today} onSelect={setPicked} countLabel={(n) => t("itemCount", { count: n })} />
      {legend.length > 0 ? <LayerChips label={t("legend")} layers={legend} selected={enabled} /> : null}
      <Freshness sources={events.state === "ok" ? events.data.sources : data.sources} onRetry={() => setAttempt((n) => n + 1)} />
      <h3 className="ui-cal-day-title">{dayTitle}</h3>
      {events.state === "loading" ? (
        <StateView kind="loading" minBlockSize="6rem" />
      ) : events.state === "failed" ? (
        <StateView kind="error" message={events.messages.join(" ") || t("failed")} />
      ) : shown.length === 0 ? (
        <p className="cal-empty">{selected === data.today && list.length > 0 ? t("nothingLeft") : t("nothing")}</p>
      ) : (
        <ul className="ui-cal-agenda">
          {shown.map((item) => (
            <ItemRow
              key={item.id}
              item={item}
              zone={zone}
              day={selected}
              now={now}
              open={open === item.id}
              onToggle={() => setOpen(open === item.id ? null : item.id)}
              onChanged={() => setAttempt((n) => n + 1)}
            />
          ))}
        </ul>
      )}
      {data.alerts.holidays.length > 0 || data.alerts.teamAwayToday > 0 ? (
        <ul className="cal-alerts">
          {data.alerts.holidays.map((h) => (
            <li key={h.date}>
              <Icon name="sun" size={14} />
              <span>
                {t("alerts.holiday", {
                  title: h.title,
                  date: new Intl.DateTimeFormat(locale, { weekday: "short", day: "numeric", month: "short", timeZone: "UTC" }).format(fromIso(h.date)),
                })}
              </span>
            </li>
          ))}
          {data.alerts.teamAwayToday > 0 ? (
            <li>
              <Icon name="people" size={14} />
              <WorkspaceLink view="team">{t("alerts.team", { count: data.alerts.teamAwayToday })}</WorkspaceLink>
            </li>
          ) : null}
        </ul>
      ) : null}
      <WorkspaceLink view="agenda" date={selected} className="cal-card-all">
        {more > 0 ? t("seeMore", { count: more }) : t("openCalendar")}
        <Icon name="chevron-forward" size={14} />
      </WorkspaceLink>
    </>,
  );
}
