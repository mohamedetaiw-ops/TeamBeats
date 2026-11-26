"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { Dialog } from "@/components/ui/Dialog";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { Switch } from "@/components/ui/Switch";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { problemMessages } from "@/lib/notifications/types";
import { LAYERS, type AdminEvent, type Attachment, type Layer, type Reminder, type Settings } from "@/lib/calendar";
import { layerOptions } from "./shared";

type Unit = { id: string; names: Record<string, string>; level: number };

const KINDS = ["company", "department", "important"] as const;
const REPEATS = ["none", "daily", "weekly", "monthly"] as const;
const ANSWERS = ["none", "rsvp", "registration"] as const;
const REMINDERS = ["none", "15m", "1h", "1d"] as const satisfies readonly Reminder[];

type Draft = {
  titleEn: string;
  titleAr: string;
  descriptionEn: string;
  descriptionAr: string;
  kind: (typeof KINDS)[number];
  orgUnitId: string;
  allDay: boolean;
  date: string;
  endDate: string;
  startTime: string;
  endTime: string;
  timeZone: string;
  location: string;
  onlineUrl: string;
  answers: (typeof ANSWERS)[number];
  capacity: string;
  waitlist: boolean;
  closesDate: string;
  closesTime: string;
  reminder: Reminder;
  repeat: (typeof REPEATS)[number];
  count: string;
};

function blank(): Draft {
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  const day = new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10);
  return { titleEn: "", titleAr: "", descriptionEn: "", descriptionAr: "", kind: "company", orgUnitId: "", allDay: false, date: day, endDate: day, startTime: "10:00", endTime: "11:00", timeZone: zone, location: "", onlineUrl: "", answers: "none", capacity: "20", waitlist: true, closesDate: "", closesTime: "", reminder: "1h", repeat: "none", count: "4" };
}

/** The local wall time of a day and HH:MM in an IANA zone, as an ISO instant with the zone's offset at that moment. */
function instant(day: string, time: string, zone: string): string {
  const guess = Date.parse(`${day}T${time}:00Z`);
  const parts = new Intl.DateTimeFormat("en-US", { timeZone: zone, timeZoneName: "longOffset" }).formatToParts(guess);
  const offset = parts.find((p) => p.type === "timeZoneName")?.value.replace("GMT", "") || "+00:00";
  return `${day}T${time}:00${offset === "" ? "+00:00" : offset}`;
}

function fromEvent(e: AdminEvent): Draft {
  const local = (iso: string | null) => {
    if (!iso) return { day: "", time: "" };
    const f = new Intl.DateTimeFormat("en-CA", { timeZone: e.timeZone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).formatToParts(new Date(iso));
    const get = (type: string) => f.find((p) => p.type === type)?.value ?? "";
    return { day: `${get("year")}-${get("month")}-${get("day")}`, time: `${get("hour")}:${get("minute")}` };
  };
  const s = local(e.start);
  const f = local(e.end);
  const closes = local(e.registrationClosesAt);
  return {
    titleEn: e.titles.en ?? "",
    titleAr: e.titles.ar ?? "",
    descriptionEn: e.descriptions.en ?? "",
    descriptionAr: e.descriptions.ar ?? "",
    kind: e.kind,
    orgUnitId: e.orgUnitId ?? "",
    allDay: e.allDay,
    date: e.allDay ? e.startDate! : s.day,
    endDate: e.allDay && e.endDate ? new Date(Date.parse(e.endDate) - 86_400_000).toISOString().slice(0, 10) : s.day,
    startTime: s.time || "10:00",
    endTime: f.time || "11:00",
    timeZone: e.timeZone,
    location: e.location ?? "",
    onlineUrl: e.onlineUrl ?? "",
    answers: e.registration ? "registration" : e.rsvp ? "rsvp" : "none",
    capacity: String(e.capacity ?? 20),
    waitlist: e.waitlist,
    closesDate: closes.day,
    closesTime: closes.time,
    reminder: e.reminder,
    repeat: e.recurrence?.frequency ?? "none",
    count: String(e.recurrence?.count ?? 4),
  };
}

function body(d: Draft) {
  const titles: Record<string, string> = { en: d.titleEn.trim() };
  if (d.titleAr.trim()) titles.ar = d.titleAr.trim();
  const descriptions: Record<string, string> = {};
  if (d.descriptionEn.trim()) descriptions.en = d.descriptionEn.trim();
  if (d.descriptionAr.trim()) descriptions.ar = d.descriptionAr.trim();
  const end = new Date(Date.parse(d.endDate || d.date) + 86_400_000).toISOString().slice(0, 10);
  return {
    titles,
    descriptions,
    kind: d.kind,
    orgUnitId: d.orgUnitId || null,
    allDay: d.allDay,
    start: d.allDay ? null : instant(d.date, d.startTime, d.timeZone),
    end: d.allDay ? null : instant(d.date, d.endTime, d.timeZone),
    startDate: d.allDay ? d.date : null,
    endDate: d.allDay ? end : null,
    timeZone: d.timeZone,
    location: d.location.trim() || null,
    onlineUrl: d.onlineUrl.trim() || null,
    audience: null,
    recurrence: d.repeat === "none" ? null : { frequency: d.repeat, interval: 1, weekdays: null, until: null, count: Number(d.count) || null },
    rsvp: d.answers === "rsvp",
    registration: d.answers === "registration",
    capacity: d.answers === "registration" ? Number(d.capacity) || null : null,
    waitlist: d.answers === "registration" && d.waitlist,
    registrationClosesAt: d.answers === "registration" && d.closesDate ? instant(d.closesDate, d.closesTime || "00:00", d.timeZone) : null,
    reminder: d.reminder,
  };
}

/**
 * Managing calendar events and settings (spec 037 US5, FR-017 to FR-021). Every change goes to the API, which checks
 * content.publish for the event's unit and hr.manage for settings; this screen only shows what it was given.
 */
export function ManageCalendar({ events: initial, settings: initialSettings }: { events: AdminEvent[] | null; settings: Settings | null }) {
  const t = useTranslations("calendar");
  const locale = useLocale();
  const [events, setEvents] = useState(initial);
  const [units, setUnits] = useState<Unit[]>([]);
  const [editing, setEditing] = useState<{ id: string | null; eTag: string | null; draft: Draft; files: Attachment[] } | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    fetch("/api/v1/org/units", { cache: "no-store" })
      .then(async (r) => (r.ok ? ((await r.json()) as Unit[]) : []))
      .then(setUnits)
      .catch(() => setUnits([]));
  }, []);

  async function reload() {
    const response = await fetch("/api/v1/calendar/admin-events", { cache: "no-store" });
    if (response.ok) setEvents((await response.json()) as AdminEvent[]);
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    if (!editing) return;
    setBusy(true);
    setErrors([]);
    try {
      const response = await fetch(editing.id ? `/api/v1/calendar/admin-events/${editing.id}` : "/api/v1/calendar/admin-events", {
        method: editing.id ? "PATCH" : "POST",
        headers: { "Content-Type": "application/json", ...(editing.eTag ? { "If-Match": editing.eTag } : {}) },
        body: JSON.stringify(body(editing.draft)),
      });
      if (response.ok) {
        setEditing(null);
        setNotice(t("manage.saved"));
        await reload();
      } else {
        const list = (await problemMessages(response)).filter(Boolean);
        setErrors(list.length ? list : [t("manage.failed")]);
      }
    } catch {
      setErrors([t("manage.failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function act(id: string, action: "cancel" | "delete") {
    const response = await fetch(action === "cancel" ? `/api/v1/calendar/admin-events/${id}/cancel` : `/api/v1/calendar/admin-events/${id}`, { method: action === "cancel" ? "POST" : "DELETE" });
    setNotice(response.ok ? t(action === "cancel" ? "manage.cancelled" : "manage.deleted") : t("manage.failed"));
    await reload();
  }

  const unitName = (id: string | null) => {
    if (!id) return t("manage.wholeOrganization");
    const u = units.find((x) => x.id === id);
    return u ? (u.names[locale] ?? u.names.en ?? Object.values(u.names)[0] ?? id) : t("manage.unit");
  };
  const when = (e: AdminEvent) => {
    if (e.allDay) {
      const f = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" });
      return t("dateRange", { start: f.format(Date.parse(e.startDate!)), end: f.format(Date.parse(e.endDate!) - 86_400_000) });
    }
    const f = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone: e.timeZone });
    return f.format(new Date(e.start!));
  };
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setEditing((current) => (current ? { ...current, draft: { ...current.draft, [key]: value } } : current));

  return (
    <div className="cal-manage">
      {notice ? (
        <Alert tone="success" title={notice} live />
      ) : null}
      {events ? (
        <section aria-labelledby="manage-events">
          <div className="cal-manage-head">
            <h2 id="manage-events">{t("manage.events")}</h2>
            <Button variant="primary" icon="plus" onClick={() => setEditing({ id: null, eTag: null, draft: blank(), files: [] })}>
              {t("manage.create")}
            </Button>
          </div>
          {events.length === 0 ? (
            <p className="cal-empty">{t("manage.none")}</p>
          ) : (
            <ul className="cal-manage-list">
              {events.map((e) => (
                <li key={e.id}>
                  <div className="cal-manage-item">
                    <bdi className="cal-manage-title">{e.titles[locale] ?? e.titles.en ?? Object.values(e.titles)[0]}</bdi>
                    <span className="cal-person-dates">
                      {when(e)} · {t(`layers.${e.kind}`)} · {unitName(e.orgUnitId)}
                      {e.recurrence ? ` · ${t("recurring")}` : ""}
                    </span>
                  </div>
                  <Badge tone={e.status === "published" ? "success" : "warning"}>{t(`manage.status.${e.status}`)}</Badge>
                  <div className="cal-actions">
                    {e.status === "published" ? (
                      <Button size="sm" onClick={() => setEditing({ id: e.id, eTag: e.eTag, draft: fromEvent(e), files: e.attachments ?? [] })}>
                        {t("manage.edit")}
                      </Button>
                    ) : null}
                    {e.status === "published" ? (
                      <ConfirmAction label={t("manage.cancel")} question={t("manage.cancelQuestion")} confirmLabel={t("manage.cancel")} cancelLabel={t("manage.keep")} onConfirm={() => act(e.id, "cancel")} />
                    ) : null}
                    <ConfirmAction label={t("manage.delete")} question={t("manage.deleteQuestion")} confirmLabel={t("manage.delete")} cancelLabel={t("manage.keep")} onConfirm={() => act(e.id, "delete")} />
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}
      {initialSettings ? <SettingsForm initial={initialSettings} /> : null}
      <Dialog open={editing !== null} onClose={() => setEditing(null)} title={editing?.id ? t("manage.edit") : t("manage.create")} size="lg">
        {editing ? (
          <form className="cal-form" onSubmit={(e) => void save(e)} noValidate>
            {errors.length > 0 ? (
              <Alert tone="danger" title={t("manage.fix")} live>
                <ul>
                  {errors.map((m) => (
                    <li key={m}>{m}</li>
                  ))}
                </ul>
              </Alert>
            ) : null}
            <div className="cal-form-row">
              <FormField label={t("manage.titleEn")} name="titleEn" value={editing.draft.titleEn} onChange={(e) => set("titleEn", e.target.value)} required />
              <FormField label={t("manage.titleAr")} name="titleAr" dir="rtl" lang="ar" value={editing.draft.titleAr} onChange={(e) => set("titleAr", e.target.value)} />
            </div>
            <div className="cal-form-row">
              <SelectField label={t("manage.kind")} name="kind" value={editing.draft.kind} onChange={(e) => set("kind", e.target.value as Draft["kind"])} options={KINDS.map((k) => ({ value: k, label: t(`layers.${k}`) }))} />
              <SelectField
                label={t("manage.unit")}
                name="orgUnitId"
                value={editing.draft.orgUnitId}
                onChange={(e) => set("orgUnitId", e.target.value)}
                options={[{ value: "", label: t("manage.wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: `${" ".repeat(Math.max(0, u.level))}${u.names[locale] ?? u.names.en ?? u.id}` }))]}
              />
            </div>
            <Switch label={t("manage.allDay")} checked={editing.draft.allDay} onChange={(e) => set("allDay", e.target.checked)} />
            <div className="cal-form-row">
              <FormField label={editing.draft.allDay ? t("manage.firstDay") : t("manage.date")} name="date" type="date" value={editing.draft.date} onChange={(e) => set("date", e.target.value)} />
              {editing.draft.allDay ? (
                <FormField label={t("manage.lastDay")} name="endDate" type="date" value={editing.draft.endDate} onChange={(e) => set("endDate", e.target.value)} />
              ) : (
                <>
                  <FormField label={t("manage.startTime")} name="startTime" type="time" value={editing.draft.startTime} onChange={(e) => set("startTime", e.target.value)} />
                  <FormField label={t("manage.endTime")} name="endTime" type="time" value={editing.draft.endTime} onChange={(e) => set("endTime", e.target.value)} />
                </>
              )}
            </div>
            <div className="cal-form-row">
              <FormField label={t("manage.timeZone")} name="timeZone" value={editing.draft.timeZone} onChange={(e) => set("timeZone", e.target.value)} hint={t("manage.timeZoneHint")} />
              <SelectField label={t("manage.repeat")} name="repeat" value={editing.draft.repeat} onChange={(e) => set("repeat", e.target.value as Draft["repeat"])} options={REPEATS.map((r) => ({ value: r, label: t(`manage.repeats.${r}`) }))} />
              {editing.draft.repeat !== "none" ? <FormField label={t("manage.count")} name="count" type="number" min={1} max={100} value={editing.draft.count} onChange={(e) => set("count", e.target.value)} /> : null}
            </div>
            <div className="cal-form-row">
              <FormField label={t("manage.location")} name="location" value={editing.draft.location} onChange={(e) => set("location", e.target.value)} />
              <FormField label={t("manage.onlineUrl")} name="onlineUrl" type="url" value={editing.draft.onlineUrl} onChange={(e) => set("onlineUrl", e.target.value)} hint={t("manage.onlineUrlHint")} />
            </div>
            <TextAreaField label={t("manage.descriptionEn")} name="descriptionEn" value={editing.draft.descriptionEn} onChange={(e) => set("descriptionEn", e.target.value)} />
            <TextAreaField label={t("manage.descriptionAr")} name="descriptionAr" dir="rtl" lang="ar" value={editing.draft.descriptionAr} onChange={(e) => set("descriptionAr", e.target.value)} />
            <div className="cal-form-row">
              <SelectField label={t("manage.answers")} name="answers" value={editing.draft.answers} onChange={(e) => set("answers", e.target.value as Draft["answers"])}
                options={ANSWERS.map((a) => ({ value: a, label: t(`manage.answerModes.${a}`) }))} />
              <SelectField label={t("manage.reminder")} name="reminder" value={editing.draft.reminder} onChange={(e) => set("reminder", e.target.value as Reminder)}
                options={REMINDERS.map((r) => ({ value: r, label: t(`manage.reminders.${r}`) }))} hint={t("manage.reminderHint")} />
            </div>
            {editing.draft.answers === "registration" ? (
              <>
                <div className="cal-form-row">
                  <FormField label={t("manage.capacity")} name="capacity" type="number" min={1} max={10000} value={editing.draft.capacity} onChange={(e) => set("capacity", e.target.value)} />
                  <FormField label={t("manage.closesDate")} name="closesDate" type="date" value={editing.draft.closesDate} onChange={(e) => set("closesDate", e.target.value)} hint={t("manage.closesHint")} />
                  <FormField label={t("manage.closesTime")} name="closesTime" type="time" value={editing.draft.closesTime} onChange={(e) => set("closesTime", e.target.value)} />
                </div>
                <Checkbox label={t("manage.waitlist")} checked={editing.draft.waitlist} onChange={(e) => set("waitlist", e.target.checked)} />
              </>
            ) : null}
            {editing.id ? <EventFiles eventId={editing.id} files={editing.files} onChange={(files) => setEditing((c) => (c ? { ...c, files } : c))} /> : null}
            <div className="cal-actions">
              <Button onClick={() => setEditing(null)}>{t("manage.close")}</Button>
              <Button type="submit" variant="primary" busy={busy} busyLabel={t("manage.saving")}>
                {t("manage.save")}
              </Button>
            </div>
          </form>
        ) : null}
      </Dialog>
    </div>
  );
}

/**
 * Files attached to an event (FR-033): each file is sent as it is chosen and the API checks its type, size and malware.
 * Only an event that exists can take files, so a new event saves first.
 */
function EventFiles({ eventId, files, onChange }: { eventId: string; files: Attachment[]; onChange: (files: Attachment[]) => void }) {
  const t = useTranslations("calendar");
  const [pending, setPending] = useState<UploadItem[]>([]);
  const [error, setError] = useState<string | null>(null);

  async function add(chosen: File[]) {
    setError(null);
    let list = files;
    for (const file of chosen) {
      const key = crypto.randomUUID();
      setPending((p) => [...p, { key, name: file.name, size: file.size, progress: 0 }]);
      try {
        const response = await fetch(`/api/v1/calendar/admin-events/${eventId}/attachments?name=${encodeURIComponent(file.name)}`, {
          method: "POST",
          headers: { "Content-Type": file.type || "application/octet-stream" },
          body: file,
        });
        if (response.ok) {
          list = [...list, (await response.json()) as Attachment];
          onChange(list);
          setPending((p) => p.filter((x) => x.key !== key));
        } else {
          const message = (await problemMessages(response)).join(" ") || t("attachments.failed");
          setPending((p) => p.map((x) => (x.key === key ? { ...x, progress: undefined, error: message } : x)));
        }
      } catch {
        setPending((p) => p.map((x) => (x.key === key ? { ...x, progress: undefined, error: t("attachments.failed") } : x)));
      }
    }
  }

  async function remove(key: string) {
    if (pending.some((p) => p.key === key)) {
      setPending((p) => p.filter((x) => x.key !== key));
      return;
    }
    const response = await fetch(`/api/v1/calendar/admin-events/${eventId}/attachments/${key}`, { method: "DELETE" });
    if (response.ok || response.status === 404) onChange(files.filter((f) => f.id !== key));
    else setError((await problemMessages(response)).join(" ") || t("attachments.failed"));
  }

  const items: UploadItem[] = [...files.map((f) => ({ key: f.id, name: f.name, size: f.size })), ...pending];
  return <FileUpload label={t("attachments.add")} hint={t("attachments.hint")} accept=".pdf,.docx,.xlsx,.pptx,.png,.jpg,.jpeg" items={items} onAdd={(f) => void add(f)} onRemove={(k) => void remove(k)} error={error} />;
}

/** Calendar settings (FR-021): the staffing threshold, indirect reports and the default layers. Versioned by the API. */
function SettingsForm({ initial }: { initial: Settings }) {
  const t = useTranslations("calendar");
  const [threshold, setThreshold] = useState(String(initial.threshold));
  const [indirect, setIndirect] = useState(initial.indirectReports);
  const [layers, setLayers] = useState<Layer[]>(initial.defaultLayers);
  const [message, setMessage] = useState<{ tone: "success" | "danger"; text: string } | null>(null);
  const [version, setVersion] = useState(initial.version);

  async function save(event: FormEvent) {
    event.preventDefault();
    const response = await fetch("/api/v1/calendar/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ threshold: Number(threshold), indirectReports: indirect, defaultLayers: layers }),
    });
    if (response.ok) {
      const saved = (await response.json()) as Settings;
      setVersion(saved.version);
      setMessage({ tone: "success", text: t("settings.saved", { version: saved.version }) });
    } else {
      setMessage({ tone: "danger", text: (await problemMessages(response)).join(" ") || t("manage.failed") });
    }
  }

  return (
    <section aria-labelledby="calendar-settings" className="cal-settings">
      <h2 id="calendar-settings">{t("settings.title")}</h2>
      <p className="cal-person-dates">{t("settings.version", { version })}</p>
      {message ? <Alert tone={message.tone} title={message.text} live /> : null}
      <form className="cal-form" onSubmit={(e) => void save(e)} noValidate>
        <FormField label={t("settings.threshold")} name="threshold" type="number" min={10} max={100} value={threshold} onChange={(e) => setThreshold(e.target.value)} hint={t("settings.thresholdHint")} />
        <Switch label={t("settings.indirect")} hint={t("settings.indirectHint")} checked={indirect} onChange={(e) => setIndirect(e.target.checked)} />
        <fieldset className="cal-fieldset">
          <legend>{t("settings.layers")}</legend>
          {layerOptions(t, [...LAYERS]).map((l) => (
            <Checkbox
              key={l.key}
              label={l.label}
              checked={layers.includes(l.key as Layer)}
              onChange={(e) => setLayers(e.target.checked ? LAYERS.filter((x) => x === l.key || layers.includes(x)) : layers.filter((x) => x !== l.key))}
            />
          ))}
        </fieldset>
        <div className="cal-actions">
          <Button type="submit" variant="primary">
            {t("settings.save")}
          </Button>
        </div>
      </form>
    </section>
  );
}
