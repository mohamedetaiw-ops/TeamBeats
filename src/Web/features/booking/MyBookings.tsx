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
            </div>
            <div className="booking-row">
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
        </div>
        <div className="booking-row">
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
