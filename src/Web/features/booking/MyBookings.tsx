"use client";

import { useState } from "react";
import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import {
  bookingApi,
  bookingDetailHref,
  bookingHref,
  calendarFileHref,
  call,
  hm,
  otherZone,
  pick,
  send,
  type BookingStatus,
  type BookingView,
  type KioskView,
  type ScanView,
} from "@/lib/booking";
import "./booking.css";

const tones: Record<BookingStatus, BadgeTone> = {
  tentative: "warning",
  confirmed: "success",
  checked_in: "info",
  released: "danger",
  cancelled: "neutral",
  rejected: "danger",
  expired: "neutral",
};

function When({ booking }: { booking: BookingView }) {
  const t = useTranslations("booking.mine");
  const format = useFormatter();
  const day = format.dateTime(new Date(`${booking.localDate}T00:00:00`), { dateStyle: "medium" });
  return (
    <span className="booking-meta">
      {t("when", { date: day, start: hm(booking.localStart), end: hm(booking.localEnd) })}
      {otherZone(booking.timeZone) ? ` ${t("zone", { zone: booking.timeZone })}` : ""}
    </span>
  );
}

/** When a booking's hold or check-in window ends, in the site's time zone. */
function useAt() {
  const format = useFormatter();
  return (iso: string, timeZone: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short", timeZone });
}

/**
 * Check-in (spec 031 US4, FR-008): offered while the window is open; the API decides whether it still is. `via`
 * says where it was done: from the booking, at a kiosk, or by the scanned code.
 */
function CheckIn({ booking, via, code, onDone, onFailed }: {
  booking: BookingView;
  via: "booking" | "kiosk" | "qr";
  code?: string;
  onDone: (b: BookingView) => void;
  onFailed: (messages: string[]) => void;
}) {
  const tc = useTranslations("booking.checkIn");
  const [busy, setBusy] = useState(false);
  if (!booking.canCheckIn) return null;
  async function run() {
    setBusy(true);
    const url = via === "qr" && code ? `${bookingApi}/scan/${code}:check-in` : `${bookingApi}/bookings/${booking.id}:check-in?via=${via}`;
    const result = await call<BookingView>(() => send(url, "POST"), tc("failed"));
    setBusy(false);
    if (!result.ok) return onFailed(result.messages);
    onDone(result.value);
  }
  return (
    <Button variant="primary" size="sm" icon="check" disabled={busy} onClick={() => void run()}>
      {tc("action")}
    </Button>
  );
}

/** Where a booking stands beyond its status: waiting for approval, rejected with a reason, or checked in. */
function Standing({ booking }: { booking: BookingView }) {
  const t = useTranslations("booking.mine");
  const at = useAt();
  if (booking.status === "tentative" && booking.holdUntil) {
    return <span className="booking-meta">{t("waiting", { until: at(booking.holdUntil, booking.timeZone) })}</span>;
  }
  if (booking.status === "rejected" && booking.decisionNote) {
    return <span className="booking-meta">{t("rejectedReason", { reason: booking.decisionNote })}</span>;
  }
  if (booking.status === "checked_in" && booking.checkedInAt) {
    return <span className="booking-meta">{t("checkedIn", { at: at(booking.checkedInAt, booking.timeZone) })}</span>;
  }
  if (booking.status === "confirmed" && booking.checkInFrom && booking.checkInUntil) {
    return <span className="booking-meta">{t("checkInWindow", { from: at(booking.checkInFrom, booking.timeZone), until: at(booking.checkInUntil, booking.timeZone) })}</span>;
  }
  return null;
}

/** My bookings (spec 031 US2): what the viewer holds or made for others, with cancel and the calendar file. */
export function MyBookings({ initial }: { initial: BookingView[] }) {
  const t = useTranslations("booking.mine");
  const ts = useTranslations("booking.status");
  const locale = useLocale();
  const [bookings, setBookings] = useState(initial);
  const [messages, setMessages] = useState<string[]>([]);

  async function cancel(booking: BookingView) {
    const result = await call<BookingView>(() => send(`${bookingApi}/bookings/${booking.id}:cancel`, "POST", {}), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setBookings((list) => list.map((b) => (b.id === booking.id ? result.value : b)));
  }

  if (bookings.length === 0) {
    return (
      <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="calendar"
        action={<ButtonLink href={bookingHref} icon="search">{t("find")}</ButtonLink>} />
    );
  }
  return (
    <div className="booking">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <ul className="booking-list" aria-label={t("listLabel")}>
        {bookings.map((b) => (
          <li key={b.id} className="booking-card">
            <h3>
              <Link href={bookingDetailHref(b.id)}>
                <bdi>{b.title ?? pick(b.resourceNames, locale)}</bdi>
              </Link>
            </h3>
            <p className="booking-meta">
              <bdi>{pick(b.resourceNames, locale)}</bdi> · <bdi>{pick(b.siteNames, locale)}</bdi>
            </p>
            <When booking={b} />
            <div className="booking-row">
              <Badge tone={tones[b.status]}>{ts(b.status)}</Badge>
              {b.seriesId ? <Badge>{t("series")}</Badge> : null}
              <Standing booking={b} />
            </div>
            <div className="booking-row">
              <CheckIn booking={b} via="booking" onFailed={setMessages}
                onDone={(done) => {
                  setMessages([]);
                  setBookings((list) => list.map((x) => (x.id === done.id ? done : x)));
                }} />
              <ButtonLink size="sm" variant="quiet" icon="download" href={calendarFileHref(b.id)}>
                {t("calendar")}
              </ButtonLink>
              {b.canCancel ? (
                <ConfirmAction label={t("cancel")} question={t("cancelQuestion")} confirmLabel={t("cancelConfirm")} cancelLabel={t("keep")} onConfirm={() => cancel(b)} />
              ) : null}
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

/**
 * One booking (spec 031 US2): its holder, the person who made it, or a facilities administrator of the site opens it.
 * An administrator cancelling someone else's booking gives a reason, which the holder is told.
 */
export function BookingDetail({ initial, own }: { initial: BookingView; own: boolean }) {
  const t = useTranslations("booking.mine");
  const ts = useTranslations("booking.status");
  const locale = useLocale();
  const [booking, setBooking] = useState(initial);
  const [reason, setReason] = useState("");
  const [series, setSeries] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function cancel() {
    const url = `${bookingApi}/bookings/${booking.id}:cancel${series ? "?series=true" : ""}`;
    const result = await call<BookingView>(() => send(url, "POST", own ? {} : { reason: reason.trim() }), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setBooking(result.value);
  }

  return (
    <div className="booking">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <section className="booking-section" aria-labelledby="booking-detail">
        <h2 id="booking-detail">
          <bdi>{pick(booking.resourceNames, locale)}</bdi>
        </h2>
        <p className="booking-meta">
          <bdi>{pick(booking.siteNames, locale)}</bdi>
        </p>
        <When booking={booking} />
        {booking.title ? <p><bdi>{booking.title}</bdi></p> : null}
        <div className="booking-row">
          <Badge tone={tones[booking.status]}>{ts(booking.status)}</Badge>
          {booking.endReason ? <span className="booking-meta">{t(`reasons.${booking.endReason}`)}</span> : null}
          <Standing booking={booking} />
        </div>
        <div className="booking-row">
          <CheckIn booking={booking} via="booking" onFailed={setMessages}
            onDone={(done) => {
              setMessages([]);
              setBooking(done);
            }} />
          <ButtonLink size="sm" icon="download" href={calendarFileHref(booking.id)}>
            {t("calendar")}
          </ButtonLink>
        </div>
      </section>
      {booking.canCancel ? (
        <section className="booking-section" aria-labelledby="booking-cancel">
          <h2 id="booking-cancel">{t("cancelTitle")}</h2>
          {!own ? <FormField label={t("reason")} name="reason" maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} required /> : null}
          {booking.seriesId ? (
            <Checkbox label={t("wholeSeries")} checked={series} onChange={(e) => setSeries(e.target.checked)} />
          ) : null}
          <div className="booking-row">
            <Button variant="destructive" icon="close" onClick={() => void cancel()}>
              {t("cancel")}
            </Button>
          </div>
        </section>
      ) : null}
    </div>
  );
}

/**
 * A scanned resource code (spec 031 US4 scenario 1, FR-008): the resource, and the viewer's booking of it for now with
 * check-in while its window is open. Someone with no booking is pointed to finding one.
 */
export function ScanCheckIn({ initial, code }: { initial: ScanView; code: string }) {
  const tc = useTranslations("booking.checkIn");
  const ts = useTranslations("booking.status");
  const locale = useLocale();
  const [booking, setBooking] = useState(initial.booking);
  const [messages, setMessages] = useState<string[]>([]);
  return (
    <div className="booking">
      {messages.length > 0 ? (
        <Alert tone="danger" title={tc("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <section className="booking-section" aria-labelledby="booking-scan">
        <h2 id="booking-scan">
          <bdi>{pick(initial.resourceNames, locale)}</bdi>
        </h2>
        <p className="booking-meta">
          <bdi>{pick(initial.siteNames, locale)}</bdi>
        </p>
        {booking ? (
          <>
            <When booking={booking} />
            <div className="booking-row">
              <Badge tone={tones[booking.status]}>{ts(booking.status)}</Badge>
              <Standing booking={booking} />
            </div>
            {booking.status === "checked_in" ? (
              <Alert tone="success" title={tc("doneTitle")} live>
                {tc("done")}
              </Alert>
            ) : null}
            <div className="booking-row">
              <CheckIn booking={booking} via="qr" code={code} onFailed={setMessages}
                onDone={(done) => {
                  setMessages([]);
                  setBooking(done);
                }} />
              <ButtonLink size="sm" variant="quiet" href={bookingDetailHref(booking.id)}>
                {tc("open")}
              </ButtonLink>
            </div>
          </>
        ) : (
          <StateView kind="empty" title={tc("noneTitle")} message={initial.checkIn ? tc("none") : tc("noneNoCheckIn")} icon="calendar"
            action={<ButtonLink href={`${bookingHref}?site=${initial.siteId}&kind=${initial.kind}`} icon="search">{tc("find")}</ButtonLink>} />
        )}
      </section>
    </div>
  );
}

/** A site's kiosk (spec 031 FR-008): the signed-in person's bookings there today, with check-in while each window is open. */
export function Kiosk({ initial }: { initial: KioskView }) {
  const tc = useTranslations("booking.checkIn");
  const ts = useTranslations("booking.status");
  const locale = useLocale();
  const [bookings, setBookings] = useState(initial.bookings);
  const [messages, setMessages] = useState<string[]>([]);
  if (bookings.length === 0) {
    return <StateView kind="empty" title={tc("kioskEmptyTitle")} message={tc("kioskEmpty")} icon="calendar" />;
  }
  return (
    <div className="booking">
      {messages.length > 0 ? (
        <Alert tone="danger" title={tc("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <ul className="booking-list" aria-label={tc("kioskLabel")}>
        {bookings.map((b) => (
          <li key={b.id} className="booking-card">
            <h3>
              <bdi>{pick(b.resourceNames, locale)}</bdi>
            </h3>
            <When booking={b} />
            <div className="booking-row">
              <Badge tone={tones[b.status]}>{ts(b.status)}</Badge>
              <Standing booking={b} />
            </div>
            <div className="booking-row">
              <CheckIn booking={b} via="kiosk" onFailed={setMessages}
                onDone={(done) => {
                  setMessages([]);
                  setBookings((list) => list.map((x) => (x.id === done.id ? done : x)));
                }} />
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
