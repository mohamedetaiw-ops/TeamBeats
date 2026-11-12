"use client";

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { IconButton } from "@/components/ui/IconButton";
import { MonthView, type CalendarEvent } from "@/components/ui/CalendarViews";
import { addDays } from "@/components/ui/dates";
import { call, send, spaceCalendarHref, spacesApi, taskHref, type SpaceAvailability, type SpaceCalendarData, type SpaceEvent } from "@/lib/spaces";
import "./spaces.css";

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
  const zone = data.timeZone;

  const events = useMemo<CalendarEvent[]>(() => {
    const list: CalendarEvent[] = [];
    for (const e of data.events) {
      const from = local(e.startsAt, zone);
      const to = local(e.endsAt, zone);
      const spans = days(from.day, addDays(to.day, 1));
      for (const day of spans) {
        list.push({ id: `${e.id}:${day}`, title: e.title, day, swatch: 1, ...(e.allDay || spans.length > 1 ? {} : { start: from.time, end: to.time }) });
      }
    }
    for (const m of data.milestones) list.push({ id: `m:${m.id}`, title: t("milestoneItem", { name: m.name }), day: m.dueOn, swatch: 3 });
    for (const task of data.tasks) list.push({ id: `t:${task.id}`, title: t("dueItem", { title: task.title }), day: task.dueOn, swatch: 5, href: taskHref(spaceId, task.id) });
    for (const a of data.away) {
      for (const day of days(a.start, a.end)) list.push({ id: `a:${a.person.userId}:${day}`, title: t("awayItem", { name: a.person.displayName }), day, swatch: 7 });
    }
    return list;
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
      <section className="spaces-section" aria-label={t("monthLabel")}>
        <MonthView month={data.from} today={today} events={events} onMonthChange={(month) => router.push(spaceCalendarHref(spaceId, month.slice(0, 7)))} />
        <p className="spaces-meta">{t("legend")}</p>
      </section>

      <div className="spaces-grid">
        <section className="spaces-section" aria-labelledby="calendar-events">
          <h2 id="calendar-events">{t("eventsTitle")}</h2>
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
        </section>

        <section className="spaces-section" aria-labelledby="calendar-busy">
          <h2 id="calendar-busy">{t("busyTitle")}</h2>
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
        </section>
      </div>
    </div>
  );
}
