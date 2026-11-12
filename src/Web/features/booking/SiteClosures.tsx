"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { bookingApi, call, send, type ClosureView } from "@/lib/booking";
import "./booking.css";

/**
 * Days a facilities administrator closes a site (spec 031 section 4 edge case): that day's bookings are cancelled and
 * the holders told the reason, and nobody can book the site that day. Holidays come from the site's working calendar.
 */
export function SiteClosures({ siteId, initial, today }: { siteId: string; initial: ClosureView[]; today: string }) {
  const t = useTranslations("booking.closures");
  const format = useFormatter();
  const [closures, setClosures] = useState(initial);
  const [date, setDate] = useState(today);
  const [reason, setReason] = useState("");
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const base = `${bookingApi}/manage/sites/${siteId}/closures`;
  const day = (iso: string) => format.dateTime(new Date(`${iso}T12:00:00Z`), { dateStyle: "full", timeZone: "UTC" });

  async function reload() {
    const fresh = await call<ClosureView[]>(() => fetch(base, { cache: "no-store" }), t("failed"));
    if (fresh.ok) setClosures(fresh.value);
  }

  async function close() {
    setNotice(null);
    const result = await call<ClosureView>(() => send(base, "POST", { date, reason: reason.trim() }), t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    setMessages([]);
    setReason("");
    setNotice(t("closed", { date: day(result.value.date), count: result.value.cancelled }));
    await reload();
  }

  async function reopen(closure: ClosureView) {
    setNotice(null);
    const result = await call<unknown>(() => send(`${base}/${closure.date}`, "DELETE"), t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    setMessages([]);
    setNotice(t("reopened", { date: day(closure.date) }));
    await reload();
  }

  return (
    <section className="booking-section" aria-labelledby="booking-closures">
      <h2 id="booking-closures">{t("title")}</h2>
      <p className="booking-meta">{t("intro")}</p>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {notice ? (
        <p role="status" className="booking-meta">
          {notice}
        </p>
      ) : null}
      <form
        className="booking-form"
        aria-label={t("formLabel")}
        onSubmit={(e) => {
          e.preventDefault();
          void close();
        }}
      >
        <div className="booking-row">
          <FormField label={t("date")} name="closure-date" type="date" value={date} min={today} onChange={(e) => setDate(e.target.value)} required />
        </div>
        <TextAreaField label={t("reason")} name="closure-reason" value={reason} maxLength={500} rows={2} onChange={(e) => setReason(e.target.value)} required />
        <div className="booking-row">
          <Button type="submit" icon="lock" disabled={!date || !reason.trim()}>
            {t("close")}
          </Button>
        </div>
      </form>
      {closures.length === 0 ? (
        <p className="booking-meta">{t("none")}</p>
      ) : (
        <ul className="booking-plain" aria-label={t("listLabel")}>
          {closures.map((c) => (
            <li key={c.date}>
              <div className="booking-row">
                <span>
                  <strong>{day(c.date)}</strong> <bdi>{c.reason}</bdi>
                </span>
                <ConfirmAction label={t("reopen")} question={t("reopenQuestion", { date: day(c.date) })} confirmLabel={t("reopenConfirm")} cancelLabel={t("keep")}
                  onConfirm={() => void reopen(c)} />
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
