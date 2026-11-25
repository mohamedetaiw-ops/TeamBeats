"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { IconButton } from "@/components/ui/IconButton";
import { AgendaRow, CalendarHeader, DayPanel, RangeMonth, type MonthBar, type Swatch } from "@/components/ui/CalendarParts";
import type { IconName } from "@/components/ui/Icon";
import { addDays } from "@/components/ui/dates";
import { call, send, spaceCalendarHref, spacesApi, taskHref, type SpaceAvailability, type SpaceCalendarData, type SpaceEvent } from "@/lib/spaces";
import "./spaces.css";
import { Widget } from "@/components/ui/Widget";

/** The day and "HH:MM" of an instant in a time zone. */
function local(instant: string, timeZone: string) {
  const parts = new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23" })
    .formatToParts(new Date(instant));
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return { day: `${get("year")}-${get("month")}-${get("day")}`, time: `${get("hour")}:${get("minute")}` };
}

/** Every day from start up to (not including) end. */
function days(start: string, end: string) {
  const list: string[] = [];
  for (let d = start; d < end && list.length < 62; d = addDays(d, 1)) list.push(d);
  return list;
}

type DayItem = { id: string; swatch: Swatch; icon: IconName; layer: "event" | "milestone" | "task" | "away"; title: string; time: string | null; href?: string };

/** The month after or before a "YYYY-MM" month. */
function shiftMonth(month: string, by: number) {
  const [y, m] = month.split("-").map(Number) as [number, number];
  return new Date(Date.UTC(y, m - 1 + by, 1)).toISOString().slice(0, 7);
}

/**
 * The team calendar of a space (spec 029 US6, FR-009): its events, milestones, task due dates and the members'
 * approved leave, never the leave type. Members add events. Free and busy for a day comes from Outlook as the viewer,
 * status only, and shows only where Outlook shares it.
 */
export function TeamCalendar({ spaceId, initial, today }: { spaceId: string; initial: SpaceCalendarData; today: string }) {
  const t = useTranslations("spaces.calendar");
  const format = useFormatter();
  const router = useRouter();
  const [data, setData] = useState(initial);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [date, setDate] = useState(today);
  const [start, setStart] = useState("10:00");
  const [end, setEnd] = useState("11:00");
  const [allDay, setAllDay] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyDay, setBusyDay] = useState(today);
  const [availability, setAvailability] = useState<SpaceAvailability | null>(null);
  const [loading, setLoading] = useState(false);
  const [panelDay, setPanelDay] = useState<string | null>(null);
  const zone = data.timeZone;
  const month = data.from.slice(0, 7);

  // Multi-day events and leave run as bars across the weeks (DLS-072); single-day items are markers named in the cell,
  // and every item of a day is a row in the day panel (DLS-071, DLS-073).
  const { bars, markers, named, byDay } = useMemo(() => {
    const bars: MonthBar[] = [];
    const markers: Record<string, Swatch[]> = {};
    const named: Record<string, { id: string; label: string; swatch: Swatch }[]> = {};
    const byDay: Record<string, DayItem[]> = {};
    const add = (day: string, item: DayItem, marker: boolean) => {
      (byDay[day] ??= []).push(item);
      if (marker) {
        (markers[day] ??= []).push(item.swatch);
        (named[day] ??= []).push({ id: item.id, label: item.title, swatch: item.swatch });
      }
    };
    for (const e of data.events) {
      const from = local(e.startsAt, zone);
      const to = local(e.endsAt, zone);
      const spans = days(from.day, addDays(to.day, 1));
      const multi = spans.length > 1;
      if (multi) bars.push({ id: `e:${e.id}`, start: from.day, end: addDays(to.day, 1), label: e.title, swatch: 1, kind: "event" });
      for (const day of spans) {
        add(day, { id: `e:${e.id}:${day}`, swatch: 1, icon: "calendar", layer: "event", title: e.title, time: e.allDay || multi ? null : t("timeRange", { start: from.time, end: to.time }) }, !multi);
      }
    }
    for (const m of data.milestones) add(m.dueOn, { id: `m:${m.id}`, swatch: 3, icon: "star", layer: "milestone", title: t("milestoneItem", { name: m.name }), time: null }, true);
    for (const task of data.tasks) {
      add(task.dueOn, { id: `t:${task.id}`, swatch: 5, icon: "tasks", layer: "task", title: t("dueItem", { title: task.title }), time: null, href: taskHref(spaceId, task.id) }, true);
    }
    for (const a of data.away) {
      const name = t("awayItem", { name: a.person.displayName });
      bars.push({ id: `a:${a.person.userId}:${a.start}`, start: a.start, end: a.end, label: name, swatch: 7, kind: "leave" });
      for (const day of days(a.start, a.end)) add(day, { id: `a:${a.person.userId}:${day}`, swatch: 7, icon: "leave", layer: "away", title: name, time: null }, false);
    }
    return { bars, markers, named, byDay };
  }, [data, zone, spaceId, t]);

  async function addEvent() {
    setNotice(null);
    const startsAt = allDay ? new Date(`${date}T00:00`) : new Date(`${date}T${start}`);
    const endsAt = allDay ? new Date(`${date}T23:59`) : new Date(`${date}T${end}`);
    const result = await call<SpaceEvent>(
      () => send(`${spacesApi}/${spaceId}/events`, "POST", { title, description, startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(), allDay }),
      t("failed"),
    );
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    setMessages([]);
    setTitle("");
    setDescription("");
    setData({ ...data, events: [...data.events, result.value].sort((a, b) => a.startsAt.localeCompare(b.startsAt)) });
    setNotice(t("added", { title: result.value.title }));
  }

  async function removeEvent(e: SpaceEvent) {
    const result = await call<void>(() => send(`${spacesApi}/${spaceId}/events/${e.id}`, "DELETE"), t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    setData({ ...data, events: data.events.filter((x) => x.id !== e.id) });
    setNotice(t("deleted", { title: e.title }));
  }

  async function loadAvailability() {
    setLoading(true);
    const result = await call<SpaceAvailability>(() => fetch(`${spacesApi}/${spaceId}/calendar/availability?date=${busyDay}`), t("failed"));
    setLoading(false);
    if (result.ok) setAvailability(result.value);
    else setMessages(result.messages);
  }

  const time = (instant: string) => format.dateTime(new Date(instant), { timeStyle: "short", timeZone: zone });

  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {notice ? (
        <p role="status" className="spaces-meta">
          {notice}
        </p>
      ) : null}
      <Widget
        title={t("monthLabel")}
        head={
          <CalendarHeader
            title={t("monthLabel")}
            period={format.dateTime(new Date(`${data.from}T00:00`), { month: "long", year: "numeric" })}
            onPrevious={() => router.push(spaceCalendarHref(spaceId, shiftMonth(month, -1)))}
            onToday={() => router.push(spaceCalendarHref(spaceId))}
            onNext={() => router.push(spaceCalendarHref(spaceId, shiftMonth(month, 1)))}
            previousLabel={t("previousMonth")}
            nextLabel={t("nextMonth")}
            todayLabel={t("thisMonth")}
          />
        }
      >
        <div className="spaces-month" data-panel={panelDay ? "open" : undefined}>
          <RangeMonth month={data.from} today={today} selected={panelDay} bars={bars} markers={markers} items={named} onSelect={(d) => setPanelDay(d)} countLabel={(n) => t("itemCount", { count: n })} />
          <DayPanel open={panelDay !== null} onClose={() => setPanelDay(null)} title={panelDay ? format.dateTime(new Date(`${panelDay}T00:00`), { dateStyle: "full" }) : ""}>
            {panelDay && (byDay[panelDay]?.length ?? 0) > 0 ? (
              <ul className="ui-cal-agenda" aria-label={t("dayItems")}>
                {byDay[panelDay]!.map((i) => (
                  <AgendaRow
                    key={i.id}
                    swatch={i.swatch}
                    icon={i.icon}
                    layerLabel={t(`layer.${i.layer}`)}
                    time={i.time ?? t("allDay")}
                    title={i.title}
                    primary={i.href ? <Link href={i.href} aria-label={t("openTaskNamed", { title: i.title })}>{t("openTask")}</Link> : undefined}
                  />
                ))}
              </ul>
            ) : (
              <p className="spaces-meta">{t("nothing")}</p>
            )}
          </DayPanel>
        </div>
        <p className="spaces-meta">{t("legend")}</p>
      </Widget>

      <div className="spaces-grid">
        <Widget title={t("eventsTitle")}>
          {data.events.length === 0 ? <p className="spaces-meta">{t("noEvents")}</p> : null}
          <ul className="spaces-list" aria-label={t("eventsTitle")}>
            {data.events.map((e) => (
              <li key={e.id} className="spaces-row">
                <bdi>{e.title}</bdi>
                <span className="spaces-meta">
                  {e.allDay
                    ? format.dateTime(new Date(e.startsAt), { dateStyle: "medium", timeZone: zone })
                    : t("eventTime", { date: format.dateTime(new Date(e.startsAt), { dateStyle: "medium", timeZone: zone }), start: time(e.startsAt), end: time(e.endsAt) })}
                </span>
                {data.canEdit ? <IconButton icon="trash" label={t("deleteEvent", { title: e.title })} size="xs" onClick={() => void removeEvent(e)} /> : null}
              </li>
            ))}
          </ul>
          {data.canEdit ? (
            <form
              className="spaces-form"
              onSubmit={(e) => {
                e.preventDefault();
                void addEvent();
              }}
            >
              <h3>{t("newEvent")}</h3>
              <FormField label={t("title")} name="title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={120} required />
              <div className="spaces-row">
                <FormField label={t("date")} name="date" type="date" value={date} onChange={(e) => setDate(e.target.value)} required />
                {allDay ? null : (
                  <>
                    <FormField label={t("start")} name="start" type="time" value={start} onChange={(e) => setStart(e.target.value)} required />
                    <FormField label={t("end")} name="end" type="time" value={end} onChange={(e) => setEnd(e.target.value)} required />
                  </>
                )}
              </div>
              <Checkbox label={t("allDay")} checked={allDay} onChange={(e) => setAllDay(e.target.checked)} />
              <TextAreaField label={t("description")} name="description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000} />
              <div className="spaces-row">
                <Button type="submit" icon="plus">
                  {t("add")}
                </Button>
              </div>
            </form>
          ) : null}
        </Widget>

        <Widget title={t("busyTitle")}>
          <p className="spaces-meta">{t("busyIntro")}</p>
          <form
            className="spaces-toolbar"
            onSubmit={(e) => {
              e.preventDefault();
              void loadAvailability();
            }}
          >
            <FormField label={t("busyDay")} name="busyDay" type="date" value={busyDay} onChange={(e) => setBusyDay(e.target.value)} required />
            <Button type="submit" variant="secondary" busy={loading} busyLabel={t("loading")}>
              {t("showBusy")}
            </Button>
          </form>
          {availability === null ? null : availability.state !== "ok" ? (
            <p className="spaces-meta">{t(`busyState.${availability.state === "turned_off" || availability.state === "not_connected" || availability.state === "reconnect" ? availability.state : "other"}`)}</p>
          ) : availability.members.length === 0 ? (
            <p className="spaces-meta">{t("busyNobody")}</p>
          ) : (
            <ul className="spaces-list" aria-label={t("busyTitle")}>
              {availability.members.map((m) => (
                <li key={m.person.userId}>
                  <bdi>{m.person.displayName}</bdi>
                  <p className="spaces-meta">
                    {!m.shared
                      ? t("notShared")
                      : m.blocks.filter((b) => b.status !== "free").length === 0
                        ? t("free")
                        : m.blocks
                            .filter((b) => b.status !== "free")
                            .map((b) => t("block", { start: time(b.start), end: time(b.end), status: t(`status.${b.status === "tentative" || b.status === "oof" || b.status === "working_elsewhere" ? b.status : "busy"}`) }))
                            .join(", ")}
                  </p>
                </li>
              ))}
            </ul>
          )}
        </Widget>
      </div>
    </div>
  );
}
