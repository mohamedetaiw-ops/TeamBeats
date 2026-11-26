"use client";

import { useState } from "react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { PlanMap, type PlanPin } from "@/components/ui/PlanMap";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import {
  bookingApi,
  bookingDetailHref,
  call,
  floorPlanEditorHref,
  otherZone,
  pick,
  send,
  type BookResult,
  type FloorMapView,
  type PlaceState,
} from "@/lib/booking";
import "./booking.css";

const tone: Record<PlaceState, "success" | "neutral" | "warning"> = { free: "success", busy: "neutral", unavailable: "warning" };

/**
 * A floor on its plan for a time (spec 031 R3 US7, FR-020, FR-021): the map, and next to it the same places as a list
 * with the same actions, which is the keyboard and screen reader way. Free means the API would let the viewer book it
 * now; booking goes through the ordinary booking request, so every rule is the API's.
 */
export function FloorMap({ initial, date: initialDate, start: initialStart, end: initialEnd }: { initial: FloorMapView; date: string; start: string; end: string }) {
  const t = useTranslations("booking.floor");
  const tk = useTranslations("booking.kinds");
  const locale = useLocale();
  const [map, setMap] = useState(initial);
  const [date, setDate] = useState(initialDate);
  const [start, setStart] = useState(initialStart);
  const [end, setEnd] = useState(initialEnd);
  const [selected, setSelected] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [booked, setBooked] = useState<BookResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const zoneNote = otherZone(map.timeZone);

  async function load() {
    setBusy("load");
    const query = new URLSearchParams({ date, start, end });
    const result = await call<FloorMapView>(() => fetch(`${bookingApi}/floors/${map.floorId}/map?${query}`), t("failed"));
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setMap(result.value);
  }

  async function book(resourceId: string) {
    setBusy(resourceId);
    setBooked(null);
    const result = await call<BookResult>(() => send(`${bookingApi}/bookings`, "POST", { resourceId, date, start, end }), t("failed"));
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setBooked(result.value);
    await load();
  }

  const name = (id: string) => pick(map.resources.find((r) => r.id === id)?.names, locale);
  const pins: PlanPin[] = map.resources
    .filter((r) => r.x !== null && r.y !== null)
    .map((r) => ({ key: r.id, x: r.x!, y: r.y!, label: t("pin", { name: pick(r.names, locale), state: t(`state.${r.state}`) }), state: r.id === selected ? "selected" : r.state }));

  return (
    <div className="booking">
      <form
        className="booking-row"
        onSubmit={(e) => {
          e.preventDefault();
          void load();
        }}
      >
        <FormField label={t("date")} name="date" type="date" value={date} onChange={(e) => setDate(e.target.value)} required />
        <FormField label={t("start")} name="start" type="time" value={start} onChange={(e) => setStart(e.target.value)} required />
        <FormField label={t("end")} name="end" type="time" value={end} onChange={(e) => setEnd(e.target.value)} required />
        <Button type="submit" busy={busy === "load"} busyLabel={t("working")}>
          {t("show")}
        </Button>
        {map.manages ? (
          <ButtonLink href={floorPlanEditorHref(map.floorId)} icon="edit" variant="quiet">
            {t("edit")}
          </ButtonLink>
        ) : null}
      </form>
      {zoneNote ? <p className="booking-meta">{t("timeZone", { zone: map.timeZone })}</p> : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {booked && booked.bookings[0] ? (
        <Alert tone="success" title={t("booked", { name: pick(booked.bookings[0].resourceNames, locale) })} live>
          <Link href={bookingDetailHref(booked.bookings[0].id)}>{t("openBooking")}</Link>
        </Alert>
      ) : null}

      <div className="booking-split">
        <Widget title={t("mapTitle")}>
          {map.plan ? (
            <>
              <PlanMap label={t("mapLabel", { floor: pick(map.names, locale) })} src={map.plan.url} alt={t("planAlt", { floor: pick(map.names, locale) })}
                width={map.plan.width} height={map.plan.height} pins={pins} onSelect={(key) => setSelected(key)} />
              <ul className="booking-legend" aria-label={t("legend")}>
                {(["free", "busy", "unavailable"] as const).map((s) => (
                  <li key={s}>
                    <Badge tone={tone[s]}>{t(`state.${s}`)}</Badge>
                  </li>
                ))}
              </ul>
              {selected ? (
                <p className="booking-meta" aria-live="polite">
                  {t("selected", { name: name(selected) })}
                </p>
              ) : null}
            </>
          ) : (
            <StateView kind="empty" title={t("noPlanTitle")} message={t("noPlan")} icon="building" />
          )}
        </Widget>

        <Widget title={t("listTitle")}>
          {map.resources.length === 0 ? (
            <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} />
          ) : (
            <ul className="booking-places" aria-label={t("listTitle")}>
              {map.resources.map((r) => (
                <li key={r.id} className="booking-row" data-selected={r.id === selected ? true : undefined}>
                  <span>
                    <bdi>{pick(r.names, locale)}</bdi>
                    <span className="booking-meta">
                      {" "}
                      {tk(r.kind)}
                      {r.zone ? ` · ${pick(r.zone, locale)}` : ""}
                      {r.capacity ? ` · ${t("seats", { count: r.capacity })}` : ""}
                      {r.x === null ? ` · ${t("notPlaced")}` : ""}
                    </span>
                  </span>
                  <Badge tone={tone[r.state]}>{t(`state.${r.state}`)}</Badge>
                  {r.state === "free" ? (
                    <Button size="sm" variant={r.id === selected ? "primary" : "secondary"} busy={busy === r.id} busyLabel={t("working")} onClick={() => void book(r.id)}>
                      {r.needsApproval ? t("request", { name: pick(r.names, locale) }) : t("book", { name: pick(r.names, locale) })}
                    </Button>
                  ) : null}
                </li>
              ))}
            </ul>
          )}
        </Widget>
      </div>
    </div>
  );
}
