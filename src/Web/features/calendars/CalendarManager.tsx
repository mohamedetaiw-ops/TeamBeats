"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { isoWeek, weekdayName, type Assignment, type Calendar, type Holiday, type WorkingDaysPreview } from "@/lib/hr/types";
import { problemMessages } from "@/lib/notifications/types";
import "./calendars.css";

type Result = { ok: boolean; messages: string[] } | null;

const json = (method: string, body: unknown, etag?: string): RequestInit => ({
  method,
  headers: { "content-type": "application/json", ...(etag ? { "if-match": etag } : {}) },
  body: JSON.stringify(body),
});

/**
 * Calendars admin (spec 022 T007): the calendar list, one calendar's working week and holidays for a year, the units
 * each calendar applies to, and a working-day preview. The API decides what the viewer may change and says so per
 * calendar (canEdit); this screen only shows its answers.
 */
export function CalendarManager({ initialCalendars, initialAssignments, units }: { initialCalendars: Calendar[]; initialAssignments: Assignment[]; units: OrgUnit[] }) {
  const t = useTranslations("calendars");
  const locale = useLocale();
  const [calendars, setCalendars] = useState(initialCalendars);
  const [assignments, setAssignments] = useState(initialAssignments);
  const [selected, setSelected] = useState<Calendar | null>(null);
  const [result, setResult] = useState<Result>(null);

  const label = (c: Calendar) => (c.isTenantDefault && Object.keys(c.name).length === 0 ? t("tenantDefault") : (c.name[locale] ?? c.name.en ?? Object.values(c.name)[0] ?? ""));

  async function reload(select?: string) {
    const [list, assigned] = await Promise.all([fetch("/api/v1/hr/calendars"), fetch("/api/v1/hr/assignments")]);
    if (list.ok) setCalendars((await list.json()) as Calendar[]);
    if (assigned.ok) setAssignments((await assigned.json()) as Assignment[]);
    if (select) await open(select);
  }

  async function open(id: string) {
    const response = await fetch(`/api/v1/hr/calendars/${id}`);
    if (response.ok) setSelected((await response.json()) as Calendar);
  }

  async function run(action: () => Promise<Response>, success: string, after?: (r: Response) => Promise<void>) {
    setResult(null);
    try {
      const response = await action();
      if (response.ok) {
        await after?.(response);
        setResult({ ok: true, messages: [success] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    }
  }

  return (
    <div className="calendars">
      <section aria-labelledby="calendar-list-title" className="calendars-section">
        <h2 id="calendar-list-title">{t("list.title")}</h2>
        <ul className="calendars-list">
          {calendars.map((c) => (
            <li key={c.id}>
              <button type="button" className="button button-secondary calendars-item" aria-pressed={selected?.id === c.id} onClick={() => void open(c.id)}>
                <bdi>{label(c)}</bdi>
                <span className="calendars-meta">
                  {c.country ? <bdi>{c.country}</bdi> : null}
                  <span>{c.weekDays.map((d) => weekdayName(d, locale)).join(t("separator"))}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
        <NewCalendar units={units} onCreated={(id) => void reload(id)} run={run} />
      </section>

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      {selected ? (
        <CalendarEditor
          key={`${selected.id}-${selected.version}`}
          calendar={selected}
          title={label(selected)}
          run={run}
          onSaved={(c) => {
            setSelected(c);
            void reload();
          }}
          onDeleted={() => {
            setSelected(null);
            void reload();
          }}
        />
      ) : null}

      <Assignments units={units} calendars={calendars} assignments={assignments} label={label} run={run} onChanged={() => void reload(selected?.id)} />
      <Preview units={units} />
    </div>
  );
}

type Run = (action: () => Promise<Response>, success: string, after?: (r: Response) => Promise<void>) => Promise<void>;

function WeekPicker({ value, onChange, legend }: { value: number[]; onChange: (days: number[]) => void; legend: string }) {
  const locale = useLocale();
  return (
    <fieldset className="calendars-week">
      <legend>{legend}</legend>
      {isoWeek.map((d) => (
        <label key={d} className="calendars-check">
          <input type="checkbox" checked={value.includes(d)} onChange={(e) => onChange(e.target.checked ? [...value, d].sort() : value.filter((x) => x !== d))} />
          <span>{weekdayName(d, locale)}</span>
        </label>
      ))}
    </fieldset>
  );
}

function NewCalendar({ units, onCreated, run }: { units: OrgUnit[]; onCreated: (id: string) => void; run: Run }) {
  const t = useTranslations("calendars");
  const locale = useLocale();
  const [en, setEn] = useState("");
  const [ar, setAr] = useState("");
  const [country, setCountry] = useState("");
  const [week, setWeek] = useState<number[]>([1, 2, 3, 4, 5]);
  const [owner, setOwner] = useState("");
  return (
    <form
      className="calendars-form"
      aria-labelledby="calendar-new-title"
      onSubmit={(e) => {
        e.preventDefault();
        void run(
          () => fetch("/api/v1/hr/calendars", json("POST", { name: { en, ar }, country: country || null, weekDays: week, ownerOrgUnitId: owner || null })),
          t("created"),
          async (r) => onCreated(((await r.json()) as Calendar).id),
        );
      }}
    >
      <h3 id="calendar-new-title">{t("new.title")}</h3>
      <div className="calendars-row">
        <FormField label={t("nameEn")} name="nameEn" lang="en" dir="ltr" value={en} onChange={(e) => setEn(e.target.value)} />
        <FormField label={t("nameAr")} name="nameAr" lang="ar" dir="rtl" value={ar} onChange={(e) => setAr(e.target.value)} />
        <FormField label={t("country")} hint={t("countryHint")} name="country" dir="ltr" maxLength={2} value={country} onChange={(e) => setCountry(e.target.value)} />
      </div>
      <WeekPicker value={week} onChange={setWeek} legend={t("week")} />
      <label className="field">
        <span>{t("owner")}</span>
        <select value={owner} onChange={(e) => setOwner(e.target.value)}>
          <option value="">{t("wholeOrganization")}</option>
          {units.map((u) => (
            <option key={u.id} value={u.id}>
              {" ".repeat(u.level * 2)}
              {unitName(u, locale)}
            </option>
          ))}
        </select>
      </label>
      <Button type="submit" variant="primary">
        {t("new.create")}
      </Button>
    </form>
  );
}

type HolidayRow = { key: string; date: string; en: string; ar: string; halfDay: boolean };

function CalendarEditor({ calendar, title, run, onSaved, onDeleted }: { calendar: Calendar; title: string; run: Run; onSaved: (c: Calendar) => void; onDeleted: () => void }) {
  const t = useTranslations("calendars");
  const [en, setEn] = useState(calendar.name.en ?? "");
  const [ar, setAr] = useState(calendar.name.ar ?? "");
  const [country, setCountry] = useState(calendar.country ?? "");
  const [week, setWeek] = useState(calendar.weekDays);
  const [year, setYear] = useState(new Date().getFullYear());
  const toRows = (y: number, list: Holiday[]) =>
    list.filter((h) => h.date.startsWith(`${y}-`)).map((h) => ({ key: h.date, date: h.date, en: h.name.en ?? "", ar: h.name.ar ?? "", halfDay: h.halfDay }));
  const [rows, setRows] = useState<HolidayRow[]>(toRows(year, calendar.holidays ?? []));
  const editable = calendar.canEdit;

  const setRow = (key: string, change: Partial<HolidayRow>) => setRows((r) => r.map((x) => (x.key === key ? { ...x, ...change } : x)));
  const saved = async (r: Response) => onSaved((await r.json()) as Calendar);

  return (
    <section className="calendars-section" aria-labelledby="calendar-edit-title">
      <h2 id="calendar-edit-title">
        <bdi>{title}</bdi>
      </h2>
      {editable ? null : <p className="notice">{t("readOnly")}</p>}
      <form
        className="calendars-form"
        onSubmit={(e) => {
          e.preventDefault();
          void run(() => fetch(`/api/v1/hr/calendars/${calendar.id}`, json("PUT", { name: { en, ar }, country: country || null, weekDays: week }, calendar.eTag)), t("saved"), saved);
        }}
      >
        <fieldset disabled={!editable} className="calendars-plain">
          <div className="calendars-row">
            <FormField label={t("nameEn")} name="editNameEn" lang="en" dir="ltr" value={en} onChange={(e) => setEn(e.target.value)} />
            <FormField label={t("nameAr")} name="editNameAr" lang="ar" dir="rtl" value={ar} onChange={(e) => setAr(e.target.value)} />
            <FormField label={t("country")} name="editCountry" dir="ltr" maxLength={2} value={country} onChange={(e) => setCountry(e.target.value)} />
          </div>
          <WeekPicker value={week} onChange={setWeek} legend={t("week")} />
          <Button type="submit" variant="primary">
            {t("save")}
          </Button>
        </fieldset>
      </form>

      <form
        className="calendars-form"
        aria-labelledby="calendar-holidays-title"
        onSubmit={(e) => {
          e.preventDefault();
          void run(
            () =>
              fetch(
                `/api/v1/hr/calendars/${calendar.id}/holidays`,
                json("PUT", { year, holidays: rows.map((r) => ({ date: r.date, name: { en: r.en, ar: r.ar }, halfDay: r.halfDay })) }, calendar.eTag),
              ),
            t("holidays.saved"),
            saved,
          );
        }}
      >
        <h3 id="calendar-holidays-title">{t("holidays.title")}</h3>
        <label className="field calendars-year">
          <span>{t("holidays.year")}</span>
          <input
            type="number"
            min={2000}
            max={2100}
            value={year}
            onChange={(e) => {
              const y = Number(e.target.value);
              setYear(y);
              setRows(toRows(y, calendar.holidays ?? []));
            }}
          />
        </label>
        <fieldset disabled={!editable} className="calendars-plain">
          <div className="calendars-table-wrap">
            <table className="calendars-table">
              <caption className="visually-hidden">{t("holidays.caption", { year })}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("holidays.date")}</th>
                  <th scope="col">{t("nameEn")}</th>
                  <th scope="col">{t("nameAr")}</th>
                  <th scope="col">{t("holidays.halfDay")}</th>
                  <th scope="col">{t("holidays.actions")}</th>
                </tr>
              </thead>
              <tbody>
                {rows.length === 0 ? (
                  <tr>
                    <td colSpan={5}>{t("holidays.none")}</td>
                  </tr>
                ) : null}
                {rows.map((r, i) => (
                  <tr key={r.key}>
                    <td>
                      <input type="date" aria-label={t("holidays.dateFor", { number: i + 1 })} value={r.date} onChange={(e) => setRow(r.key, { date: e.target.value })} />
                    </td>
                    <td>
                      <input lang="en" dir="ltr" aria-label={t("holidays.nameEnFor", { number: i + 1 })} value={r.en} onChange={(e) => setRow(r.key, { en: e.target.value })} />
                    </td>
                    <td>
                      <input lang="ar" dir="rtl" aria-label={t("holidays.nameArFor", { number: i + 1 })} value={r.ar} onChange={(e) => setRow(r.key, { ar: e.target.value })} />
                    </td>
                    <td>
                      <input type="checkbox" aria-label={t("holidays.halfDayFor", { number: i + 1 })} checked={r.halfDay} onChange={(e) => setRow(r.key, { halfDay: e.target.checked })} />
                    </td>
                    <td>
                      <button type="button" className="button button-quiet" onClick={() => setRows((x) => x.filter((y) => y.key !== r.key))}>
                        {t("holidays.remove", { number: i + 1 })}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="calendars-actions">
            <Button onClick={() => setRows((x) => [...x, { key: crypto.randomUUID(), date: `${year}-01-01`, en: "", ar: "", halfDay: false }])}>{t("holidays.add")}</Button>
            <Button type="submit" variant="primary">
              {t("holidays.save")}
            </Button>
          </div>
        </fieldset>
      </form>

      {editable && !calendar.isTenantDefault ? (
        <ConfirmAction
          label={t("delete")}
          question={t("deleteQuestion")}
          confirmLabel={t("deleteConfirm")}
          cancelLabel={t("cancel")}
          onConfirm={() => run(() => fetch(`/api/v1/hr/calendars/${calendar.id}`, { method: "DELETE" }), t("deleted"), async () => onDeleted())}
        />
      ) : null}
    </section>
  );
}

function Assignments({
  units,
  calendars,
  assignments,
  label,
  run,
  onChanged,
}: {
  units: OrgUnit[];
  calendars: Calendar[];
  assignments: Assignment[];
  label: (c: Calendar) => string;
  run: Run;
  onChanged: () => void;
}) {
  const t = useTranslations("calendars");
  const locale = useLocale();
  const byUnit = new Map(assignments.map((a) => [a.orgUnitId, a.calendarId]));
  const choices = calendars.filter((c) => !c.isTenantDefault);
  return (
    <section className="calendars-section" aria-labelledby="calendar-assign-title">
      <h2 id="calendar-assign-title">{t("assign.title")}</h2>
      <p className="field-hint">{t("assign.hint")}</p>
      <div className="calendars-table-wrap">
        <table className="calendars-table">
          <caption className="visually-hidden">{t("assign.title")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("assign.unit")}</th>
              <th scope="col">{t("assign.calendar")}</th>
            </tr>
          </thead>
          <tbody>
            {units.map((u) => (
              <tr key={u.id}>
                <th scope="row" style={{ paddingInlineStart: `${u.level + 0.5}rem` }}>
                  <bdi>{unitName(u, locale)}</bdi>
                </th>
                <td>
                  <select
                    aria-label={t("assign.calendarFor", { unit: unitName(u, locale) })}
                    value={byUnit.get(u.id) ?? ""}
                    onChange={(e) =>
                      void run(
                        () => fetch(`/api/v1/hr/assignments/${u.id}`, json("PUT", { calendarId: e.target.value || null })),
                        t("assign.saved"),
                        async () => onChanged(),
                      )
                    }
                  >
                    <option value="">{t("assign.inherit")}</option>
                    {choices.map((c) => (
                      <option key={c.id} value={c.id}>
                        {label(c)}
                      </option>
                    ))}
                  </select>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function Preview({ units }: { units: OrgUnit[] }) {
  const t = useTranslations("calendars");
  const locale = useLocale();
  const today = new Date().toISOString().slice(0, 10);
  const [unit, setUnit] = useState("");
  const [from, setFrom] = useState(today);
  const [to, setTo] = useState(today);
  const [answer, setAnswer] = useState<WorkingDaysPreview | "failed" | null>(null);
  return (
    <section className="calendars-section" aria-labelledby="calendar-preview-title">
      <h2 id="calendar-preview-title">{t("preview.title")}</h2>
      <form
        className="calendars-row"
        onSubmit={async (e) => {
          e.preventDefault();
          const query = new URLSearchParams({ from, to, ...(unit ? { orgUnitId: unit } : {}) });
          const response = await fetch(`/api/v1/hr/working-days?${query}`);
          setAnswer(response.ok ? ((await response.json()) as WorkingDaysPreview) : "failed");
        }}
      >
        <label className="field">
          <span>{t("assign.unit")}</span>
          <select value={unit} onChange={(e) => setUnit(e.target.value)}>
            <option value="">{t("wholeOrganization")}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {" ".repeat(u.level * 2)}
                {unitName(u, locale)}
              </option>
            ))}
          </select>
        </label>
        <FormField label={t("preview.from")} name="from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        <FormField label={t("preview.to")} name="to" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        <Button type="submit">{t("preview.run")}</Button>
      </form>
      <p role="status" className="calendars-answer">
        {answer === null ? "" : answer === "failed" ? t("preview.failed") : t("preview.result", { count: answer.workingDays })}
      </p>
    </section>
  );
}
