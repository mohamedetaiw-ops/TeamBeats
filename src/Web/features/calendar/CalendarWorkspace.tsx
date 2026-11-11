"use client";

import { useEffect, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { AvailabilityHeatmap, CalendarHeader, CalendarStats, DayPanel, LayerChips, RangeMonth, WeekStrip, type MonthBar } from "@/components/ui/CalendarParts";
import { WeekView, type CalendarEvent } from "@/components/ui/CalendarViews";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { Badge } from "@/components/ui/Badge";
import { Avatar } from "@/components/ui/Avatar";
import { ButtonLink } from "@/components/ui/Button";
import { addDays, addMonths, firstWeekday, fromIso, monthStart } from "@/components/ui/dates";
import { useHydrated, useNow } from "@/lib/clock";
import {
  LAYER_LOOK,
  LAYERS,
  VIEWS,
  daysFrom,
  isoDay,
  weekOf,
  zoneDay,
  type CalendarItem,
  type CalendarView,
  type EventsAnswer,
  type Layer,
  type Preferences,
  type TeamAnswer,
} from "@/lib/calendar";
import { Freshness, ItemRow, itemsOn, layerOptions, useCalendar } from "./shared";

const AGENDA_DAYS = 14;

/**
 * The calendar workspace (spec 037 FR-006, US2 to US4): agenda, week, month and, for managers, team availability.
 * The view and date live in the URL so a link opens the same place. Layer chips filter what is shown and are saved as
 * the person's preference; turning a layer off never changes the data under it. Everything shown comes from the
 * calendar API, which applies every permission on the server.
 */
export function CalendarWorkspace({ canManage }: { canManage: boolean }) {
  const t = useTranslations("calendar");
  const locale = useLocale();
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const hydrated = useHydrated();
  const now = useNow();
  const weekStart = firstWeekday(locale);
  const [attempt, setAttempt] = useState(0);
  const [prefs, setPrefs] = useState<Preferences | null>(null);
  const [panelDay, setPanelDay] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);

  const prefLoad = useCalendar<Preferences>("/api/v1/calendar/preferences");
  useEffect(() => {
    if (prefLoad.state === "ok") setPrefs(prefLoad.data);
  }, [prefLoad]);

  const asked = params.get("view");
  const view: CalendarView = (VIEWS as readonly string[]).includes(asked ?? "") ? (asked as CalendarView) : prefs?.view ?? "agenda";
  const browserToday = hydrated && now !== null ? new Date(now).toISOString().slice(0, 10) : null;
  const dateParam = params.get("date");
  const date = isoDay(dateParam) ? dateParam : browserToday;

  const go = (next: { view?: CalendarView; date?: string | null }) => {
    const query = new URLSearchParams(params.toString());
    if (next.view) query.set("view", next.view);
    if (next.date === null) query.delete("date");
    else if (next.date) query.set("date", next.date);
    router.replace(`${pathname}?${query.toString()}`, { scroll: false });
  };

  const range = (() => {
    if (!date) return null;
    if (view === "agenda") return { from: date, to: addDays(date, AGENDA_DAYS) };
    if (view === "week") {
      const s = weekOf(date, weekStart);
      return { from: s, to: addDays(s, 7) };
    }
    const first = weekOf(monthStart(date, "gregory"), weekStart);
    return { from: first, to: addDays(first, 42) };
  })();
  const layers = prefs ? prefs.layers.join(",") : null;
  const eventsUrl = range && prefs && view !== "team" ? `/api/v1/calendar/events?from=${range.from}&to=${range.to}&layers=${layers || "none"}${attempt > 0 ? "&refresh=true" : ""}` : null;
  const events = useCalendar<EventsAnswer>(prefs && prefs.layers.length === 0 ? null : eventsUrl, attempt);
  const teamUrl = range && view === "team" ? `/api/v1/calendar/team-availability?from=${weekOf(date!, weekStart)}&to=${addDays(weekOf(date!, weekStart), 28)}` : null;
  const team = useCalendar<TeamAnswer>(teamUrl, attempt);

  async function toggle(layer: string) {
    if (!prefs) return;
    const next = prefs.layers.includes(layer as Layer) ? prefs.layers.filter((l) => l !== layer) : LAYERS.filter((l) => l === layer || prefs.layers.includes(l));
    setPrefs({ ...prefs, layers: next });
    await fetch("/api/v1/calendar/preferences", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ layers: next, view: prefs.view }) }).catch(() => undefined);
  }

  async function saveView(next: CalendarView) {
    go({ view: next });
    if (prefs) {
      setPrefs({ ...prefs, view: next });
      await fetch("/api/v1/calendar/preferences", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ layers: prefs.layers, view: next }) }).catch(() => undefined);
    }
  }

  if (prefLoad.state === "failed") return <StateView kind="error" message={prefLoad.messages.join(" ") || t("failed")} />;
  if (!prefs || !date || now === null || !range) return <StateView kind="loading" minBlockSize="20rem" />;

  const available = LAYERS.filter((l) => prefs.available.includes(l));
  const views = VIEWS.filter((v) => v !== "team" || prefs.available.includes("team_leave"));
  const zone = events.state === "ok" ? events.data.timeZone : team.state === "ok" ? team.data.timeZone : "UTC";
  const today = zoneDay(now, zone);
  const step = view === "agenda" ? AGENDA_DAYS : view === "week" || view === "team" ? 7 : 0;
  const move = (dir: 1 | -1) => go({ date: step ? addDays(date, dir * step) : addMonths(date, dir, "gregory") });
  const monthYear = new Intl.DateTimeFormat(locale, { month: "long", year: "numeric", timeZone: "UTC" });
  const short = new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", timeZone: "UTC" });
  const period =
    view === "month" ? monthYear.format(fromIso(date)) : t("dateRange", { start: short.format(fromIso(range.from)), end: short.format(fromIso(addDays(view === "team" ? addDays(weekOf(date, weekStart), 28) : range.to, -1))) });

  const items = events.state === "ok" ? events.data.items : [];
  const dayRows = (day: string) => (
    <ul className="ui-cal-agenda">
      {itemsOn(items, day, zone).map((item) => (
        <ItemRow
          key={`${day}-${item.id}`}
          item={item}
          zone={zone}
          day={day}
          now={now}
          open={open === `${day}-${item.id}`}
          onToggle={() => setOpen(open === `${day}-${item.id}` ? null : `${day}-${item.id}`)}
          onChanged={() => setAttempt((n) => n + 1)}
        />
      ))}
    </ul>
  );
  const dayTitle = (day: string) =>
    day === today ? t("todayWith", { date: short.format(fromIso(day)) }) : new Intl.DateTimeFormat(locale, { weekday: "long", day: "numeric", month: "long", timeZone: "UTC" }).format(fromIso(day));

  let body: React.ReactNode;
  if (view === "team") {
    body = team.state === "loading" ? <StateView kind="loading" minBlockSize="16rem" /> : team.state === "failed" ? <StateView kind="error" message={team.messages.join(" ") || t("team.none")} /> : <TeamView data={team.data} weekStart={weekStart} />;
  } else if (prefs.layers.length === 0) {
    body = <StateView kind="empty" title={t("noLayers")} message={t("noLayersHint")} />;
  } else if (events.state === "loading") {
    body = <StateView kind="loading" minBlockSize="16rem" />;
  } else if (events.state === "failed") {
    body = <StateView kind="error" message={events.messages.join(" ") || t("failed")} />;
  } else if (view === "agenda") {
    const days = daysFrom(range.from, AGENDA_DAYS).filter((d) => itemsOn(items, d, zone).length > 0);
    body =
      days.length === 0 ? (
        <StateView kind="empty" title={t("nothingRange")} message="" />
      ) : (
        <div className="cal-agenda">
          {days.map((d) => (
            <section key={d} aria-labelledby={`day-${d}`}>
              <h3 id={`day-${d}`} className="ui-cal-day-title">
                {dayTitle(d)}
              </h3>
              {dayRows(d)}
            </section>
          ))}
        </div>
      );
  } else if (view === "week") {
    const strip = daysFrom(range.from, 7).map((d) => {
      const on = itemsOn(items, d, zone);
      return { date: d, markers: LAYERS.filter((l) => on.some((i) => i.layer === l)).map((l) => LAYER_LOOK[l].swatch), count: on.length };
    });
    const chips: CalendarEvent[] = items.flatMap((i) =>
      daysFrom(range.from, 7)
        .filter((d) => itemsOn([i], d, zone).length > 0)
        .map((d) => toChip(i, d, zone, t)),
    );
    body = (
      <div className="cal-week">
        <WeekStrip label={t("weekLabel")} days={strip} selected={date} today={today} onSelect={(d) => go({ date: d })} countLabel={(n) => t("itemCount", { count: n })} />
        <div className="cal-week-grid">
          <WeekView start={range.from} events={chips} today={today} startHour={7} endHour={20} />
        </div>
        <h3 className="ui-cal-day-title">{dayTitle(date)}</h3>
        {dayRows(date)}
      </div>
    );
  } else {
    const bars: MonthBar[] = items
      .filter((i) => i.allDay && i.startDate && i.endDate && (i.layer === "holiday" || i.layer === "my_leave" || Date.parse(i.endDate) - Date.parse(i.startDate) > 86_400_000))
      .map((i) => ({
        id: i.id,
        start: i.startDate!,
        end: i.endDate!,
        label: i.title ?? (i.layer === "my_leave" ? t("onLeave") : t("busy")),
        swatch: LAYER_LOOK[i.layer].swatch,
        kind: i.layer === "holiday" ? "holiday" : i.layer === "my_leave" ? "leave" : "event",
        pending: i.status === "pending",
      }));
    const barIds = new Set(bars.map((b) => b.id));
    const markers: Record<string, number[]> = {};
    for (const d of daysFrom(range.from, 42)) {
      markers[d] = itemsOn(items, d, zone)
        .filter((i) => !barIds.has(i.id))
        .map((i) => LAYER_LOOK[i.layer].swatch);
    }
    body = (
      <div className="cal-month" data-panel={panelDay ? "open" : undefined}>
        <RangeMonth month={date} today={today} selected={panelDay} weekStart={weekStart} bars={bars} markers={markers} onSelect={(d) => setPanelDay(d)} countLabel={(n) => t("itemCount", { count: n })} />
        <DayPanel open={panelDay !== null} onClose={() => setPanelDay(null)} title={panelDay ? dayTitle(panelDay) : ""}>
          {panelDay && itemsOn(items, panelDay, zone).length === 0 ? <p className="cal-empty">{t("nothing")}</p> : panelDay ? dayRows(panelDay) : null}
        </DayPanel>
      </div>
    );
  }

  return (
    <div className="cal-workspace">
      <CalendarHeader
        title={t(`views.${view}`)}
        period={period}
        previousLabel={t("previous")}
        nextLabel={t("next")}
        todayLabel={t("todayButton")}
        onPrevious={() => move(-1)}
        onNext={() => move(1)}
        onToday={() => go({ date: null })}
      >
        <div className="cal-toolbar">
          <SegmentedControl label={t("views.label")} name="calendar-view" value={view} options={views.map((v) => ({ value: v, label: t(`views.${v}`) }))} onChange={(v) => void saveView(v as CalendarView)} />
          {canManage ? (
            <ButtonLink href="/calendar/manage" size="sm" variant="quiet" icon="settings">
              {t("manage.open")}
            </ButtonLink>
          ) : null}
        </div>
      </CalendarHeader>
      {view !== "team" ? <LayerChips label={t("layersLabel")} layers={layerOptions(t, available)} selected={prefs.layers} onToggle={(l) => void toggle(l)} /> : null}
      {events.state === "ok" && view !== "team" ? <Freshness sources={events.data.sources} onRetry={() => setAttempt((n) => n + 1)} /> : null}
      {body}
    </div>
  );
}

function toChip(item: CalendarItem, day: string, zone: string, t: (key: string, values?: Record<string, string | number>) => string): CalendarEvent {
  const hm = (iso: string) => new Intl.DateTimeFormat("en-GB", { timeZone: zone, hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(new Date(iso));
  const sameDay = !item.allDay && zoneDay(item.start!, zone) === day && zoneDay(item.end!, zone) === day;
  const title = item.layer === "team_leave" ? t("teamAway", { count: item.count ?? 0 }) : item.title ?? (item.isPrivate ? t("private") : t("busy"));
  return {
    id: `${item.id}-${day}`,
    title: item.status === "cancelled" ? t("cancelledTitle", { title }) : title,
    day,
    start: sameDay ? hm(item.start!) : undefined,
    end: sameDay ? hm(item.end!) : undefined,
    swatch: LAYER_LOOK[item.layer].swatch,
  };
}

/** Team availability (spec 037 US4, DLS-074): counts, the heatmap with the threshold, upcoming leave and the people. */
function TeamView({ data, weekStart }: { data: TeamAnswer; weekStart: number }) {
  const t = useTranslations("calendar");
  const locale = useLocale();
  const short = new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", timeZone: "UTC" });
  const long = new Intl.DateTimeFormat(locale, { dateStyle: "full", timeZone: "UTC" });
  const breaches = data.days.filter((d) => d.breach).length;
  return (
    <div className="cal-team">
      <CalendarStats
        items={[
          { key: "available", value: t("team.ofTotal", { available: data.todayCounts.available, total: data.todayCounts.total }), label: t("team.availableToday") },
          { key: "leave", value: String(data.todayCounts.onLeave), label: t("team.onLeaveToday") },
          { key: "soon", value: String(data.todayCounts.startingSoon), label: t("team.startingSoon") },
        ]}
      />
      <section aria-labelledby="team-heat">
        <h3 id="team-heat" className="ui-cal-day-title">
          {t("team.heatTitle", { threshold: data.threshold })}
        </h3>
        {breaches > 0 ? <p className="cal-warning">{t("team.breaches", { count: breaches, threshold: data.threshold })}</p> : null}
        <AvailabilityHeatmap
          label={t("team.heatTitle", { threshold: data.threshold })}
          weekStart={weekStart}
          cells={data.days.map((d) => ({
            date: d.date,
            working: d.working,
            share: d.share,
            breach: d.breach,
            detail: d.working ? t("team.cell", { date: long.format(fromIso(d.date)), available: d.available, total: d.total }) : t("team.nonWorking"),
          }))}
          breachLabel={t("team.underThreshold")}
          nonWorkingLabel={t("team.nonWorking")}
          shareLabel={(s) => t("team.share", { share: s })}
        />
      </section>
      <section aria-labelledby="team-upcoming">
        <h3 id="team-upcoming" className="ui-cal-day-title">
          {t("team.upcoming")}
        </h3>
        {data.upcoming.length === 0 ? (
          <p className="cal-empty">{t("team.noUpcoming")}</p>
        ) : (
          <ul className="cal-people">
            {data.upcoming.map((u) => (
              <li key={`${u.userId}-${u.start}`}>
                <Avatar name={u.displayName} size="sm" />
                <span className="cal-person">
                  <bdi>{u.displayName}</bdi>
                  <span className="cal-person-dates">
                    {t("dateRange", { start: short.format(fromIso(u.start)), end: short.format(fromIso(addDays(u.end, -1))) })}
                    {u.typeName ? ` · ${u.typeName}` : ""}
                  </span>
                </span>
                <Badge tone={u.status === "approved" ? "success" : "warning"}>{t(`team.status.${u.status}`)}</Badge>
              </li>
            ))}
          </ul>
        )}
      </section>
      {data.more > 0 ? <p className="cal-empty">{t("team.more", { count: data.more })}</p> : null}
    </div>
  );
}
