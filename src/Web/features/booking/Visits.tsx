"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import {
  bookingApi,
  call,
  hm,
  localTime,
  otherZone,
  pick,
  send,
  type BookingView,
  type SiteView,
  type VisitStatus,
  type VisitView,
} from "@/lib/booking";
import "./booking.css";

export const visitTones: Record<VisitStatus, BadgeTone> = { expected: "info", arrived: "success", left: "neutral", cancelled: "neutral" };

type Draft = { site: string; date: string; start: string; end: string; name: string; email: string; company: string; booking: string };

const live = new Set(["tentative", "confirmed", "checked_in"]);

/** When a visit is, in its site's time zone, with the zone named when it is not the reader's. */
export function VisitWhen({ visit }: { visit: VisitView }) {
  const t = useTranslations("booking.visits");
  const format = useFormatter();
  const day = format.dateTime(new Date(`${visit.date}T00:00:00`), { dateStyle: "medium" });
  return (
    <span className="booking-meta">
      {t("when", { date: day, start: localTime(visit.startsAt, visit.timeZone), end: localTime(visit.endsAt, visit.timeZone) })}
      {otherZone(visit.timeZone) ? ` ${t("zone", { zone: visit.timeZone })}` : ""}
    </span>
  );
}

/**
 * Visitors for hosts (spec 031 R3 US8, FR-022, FR-023): expect a visitor at a site for a time, optionally with one of the
 * host's own bookings there that day, and change or cancel the visit. The visitor is emailed their code by the API; the
 * page shows the code and whether the email went out, so the host can pass it on when it did not. Every rule is the API's.
 */
export function Visits({ initial, sites, bookings, today }: { initial: VisitView[]; sites: SiteView[]; bookings: BookingView[]; today: string }) {
  const t = useTranslations("booking.visits");
  const locale = useLocale();
  const blank: Draft = { site: sites[0]?.id ?? "", date: today, start: "10:00", end: "11:00", name: "", email: "", company: "", booking: "" };
  const [visits, setVisits] = useState(initial);
  const [draft, setDraft] = useState<Draft>(blank);
  const [editing, setEditing] = useState<VisitView | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (field: keyof Draft, value: string) => setDraft((d) => ({ ...d, [field]: value, ...(field === "site" || field === "date" ? { booking: "" } : {}) }));
  const mine = bookings.filter((b) => b.siteId === draft.site && b.localDate === draft.date && live.has(b.status));

  function edit(visit: VisitView) {
    setEditing(visit);
    setDone(null);
    setDraft({
      site: visit.siteId, date: visit.date, start: localTime(visit.startsAt, visit.timeZone), end: localTime(visit.endsAt, visit.timeZone), name: visit.name ?? "",
      email: visit.email ?? "", company: visit.company ?? "", booking: visit.bookingId ?? "",
    });
  }

  async function save() {
    setBusy(true);
    setDone(null);
    const body = {
      siteId: draft.site, date: draft.date, start: draft.start, end: draft.end, name: draft.name, email: draft.email, company: draft.company || null,
      bookingId: draft.booking || null, locale, version: editing?.version,
    };
    const result = await call<VisitView>(() => (editing ? send(`${bookingApi}/visits/${editing.id}`, "PUT", body) : send(`${bookingApi}/visits`, "POST", body)), t("failed"));
    setBusy(false);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setVisits((list) => [...list.filter((v) => v.id !== result.value.id), result.value].sort((a, b) => a.startsAt.localeCompare(b.startsAt)));
    setDone(editing ? t("changed", { name: result.value.name ?? "" }) : t("registered", { name: result.value.name ?? "" }));
    setEditing(null);
    setDraft({ ...blank, site: draft.site, date: draft.date });
  }

  async function cancel(visit: VisitView) {
    const result = await call<VisitView>(() => send(`${bookingApi}/visits/${visit.id}:cancel`, "POST"), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setDone(null);
    setVisits((list) => list.map((v) => (v.id === visit.id ? result.value : v)));
  }

  return (
    <div className="booking">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {done ? <Alert tone="success" title={done} live /> : null}

      <Widget title={editing ? t("editTitle") : t("formTitle")}>
        {sites.length === 0 ? (
          <StateView kind="empty" title={t("noSitesTitle")} message={t("noSites")} icon="building" />
        ) : (
          <form
            className="booking-form"
            aria-label={editing ? t("editTitle") : t("formTitle")}
            onSubmit={(e) => {
              e.preventDefault();
              void save();
            }}
          >
            <div className="booking-row">
              <SelectField label={t("site")} name="site" value={draft.site} onChange={(e) => set("site", e.target.value)} disabled={editing !== null}
                options={sites.map((s) => ({ value: s.id, label: pick(s.names, locale) }))} />
              <FormField label={t("date")} name="date" type="date" min={today} value={draft.date} onChange={(e) => set("date", e.target.value)} required />
              <FormField label={t("start")} name="start" type="time" value={draft.start} onChange={(e) => set("start", e.target.value)} required />
              <FormField label={t("end")} name="end" type="time" value={draft.end} onChange={(e) => set("end", e.target.value)} required />
            </div>
            <div className="booking-row">
              <FormField label={t("name")} name="visitorName" value={draft.name} maxLength={200} autoComplete="off" onChange={(e) => set("name", e.target.value)} required />
              <FormField label={t("email")} name="visitorEmail" type="email" value={draft.email} maxLength={320} autoComplete="off" onChange={(e) => set("email", e.target.value)} required />
              <FormField label={t("company")} name="visitorCompany" value={draft.company} maxLength={200} autoComplete="off" hint={t("optional")}
                onChange={(e) => set("company", e.target.value)} />
            </div>
            {mine.length > 0 ? (
              <SelectField label={t("booking")} name="booking" value={draft.booking} onChange={(e) => set("booking", e.target.value)}
                options={[{ value: "", label: t("noBooking") }, ...mine.map((b) => ({ value: b.id, label: t("bookingOption", { name: pick(b.resourceNames, locale), start: hm(b.localStart), end: hm(b.localEnd) }) }))]} />
            ) : null}
            <p className="booking-meta">{t("privacy")}</p>
            <div className="booking-row">
              <Button type="submit" variant="primary" icon={editing ? "check" : "plus"} busy={busy} busyLabel={t("working")}>
                {editing ? t("save") : t("register")}
              </Button>
              {editing ? (
                <Button variant="quiet" onClick={() => { setEditing(null); setDraft(blank); }}>
                  {t("stopEditing")}
                </Button>
              ) : null}
            </div>
          </form>
        )}
      </Widget>

      <Widget title={t("listTitle")}>
        {visits.length === 0 ? (
          <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="user" />
        ) : (
          <ul className="booking-list" aria-label={t("listTitle")}>
            {visits.map((v) => (
              <li key={v.id} className="booking-card">
                <h3>
                  <bdi>{v.name ?? t("removedName")}</bdi>
                </h3>
                <p className="booking-meta">
                  <bdi>{pick(v.siteNames, locale)}</bdi>
                  {v.company ? <> · <bdi>{v.company}</bdi></> : null}
                </p>
                <VisitWhen visit={v} />
                <div className="booking-row">
                  <Badge tone={visitTones[v.status]}>{t(`status.${v.status}`)}</Badge>
                  {v.status === "expected" && v.emailState !== "sent" ? (
                    <Badge tone={v.emailState === "pending" ? "warning" : "danger"}>{t(`emailState.${v.emailState}`)}</Badge>
                  ) : null}
                  {v.endReason === "left_company" ? <span className="booking-meta">{t("hostLeft")}</span> : null}
                </div>
                {v.code ? (
                  <p className="booking-meta">
                    {t("code")} <strong className="booking-code">{v.code}</strong>
                  </p>
                ) : null}
                {v.status === "expected" ? (
                  <div className="booking-row">
                    <Button size="sm" variant="secondary" icon="edit" onClick={() => edit(v)}>
                      {t("change", { name: v.name ?? "" })}
                    </Button>
                    <ConfirmAction label={t("cancel", { name: v.name ?? "" })} question={t("cancelQuestion")} confirmLabel={t("cancelConfirm")} cancelLabel={t("keep")}
                      onConfirm={() => cancel(v)} />
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Widget>
    </div>
  );
}
