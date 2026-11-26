"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import { bookingApi, call, localTime, pick, receptionHref, send, type ReceptionDay, type ReceptionSite, type VisitView } from "@/lib/booking";
import { VisitWhen, visitTones } from "./Visits";
import "./booking.css";

/**
 * Reception for a site (spec 031 R3 US8, FR-024): the day's visitors with their host and time, check-in by the code the
 * visitor brings or from the list, and check-out. Codes work only on the visit day; the API checks that and the scope.
 */
export function Reception({ sites, initial }: { sites: ReceptionSite[]; initial: ReceptionDay }) {
  const t = useTranslations("booking.reception");
  const tv = useTranslations("booking.visits");
  const locale = useLocale();
  const router = useRouter();
  const [day, setDay] = useState(initial);
  const [date, setDate] = useState(initial.date);
  const [code, setCode] = useState("");
  const [messages, setMessages] = useState<string[]>([]);
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  async function load(next: string) {
    setBusy("load");
    const result = await call<ReceptionDay>(() => fetch(`${bookingApi}/reception/sites/${day.siteId}/visits?date=${next}`), t("failed"));
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setDay(result.value);
  }

  function shown(visit: VisitView, message: string) {
    setMessages([]);
    setDone(message);
    setDay((d) => ({ ...d, visits: d.visits.map((v) => (v.id === visit.id ? visit : v)) }));
  }

  async function byCode() {
    setBusy("code");
    setDone(null);
    const result = await call<VisitView>(() => send(`${bookingApi}/reception/sites/${day.siteId}/visits:check-in`, "POST", { code }), t("failed"));
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    setCode("");
    if (day.today) shown(result.value, t("arrived", { name: result.value.name ?? "" }));
    else {
      setDone(t("arrived", { name: result.value.name ?? "" }));
      await load(result.value.date);
    }
  }

  async function act(visit: VisitView, action: "check-in" | "check-out") {
    setBusy(visit.id);
    setDone(null);
    const result = await call<VisitView>(() => send(`${bookingApi}/reception/visits/${visit.id}:${action}`, "POST"), t("failed"));
    setBusy(null);
    if (!result.ok) return setMessages(result.messages);
    shown(result.value, action === "check-in" ? t("arrived", { name: result.value.name ?? "" }) : t("left", { name: result.value.name ?? "" }));
  }

  const waiting = day.visits.filter((v) => v.status === "expected").length;
  const inside = day.visits.filter((v) => v.status === "arrived").length;

  return (
    <div className="booking">
      <div className="booking-row">
        {sites.length > 1 ? (
          <SelectField label={t("site")} name="site" value={day.siteId} onChange={(e) => router.push(receptionHref(e.target.value))}
            options={sites.map((s) => ({ value: s.id, label: pick(s.names, locale) }))} />
        ) : null}
        <form
          className="booking-row"
          onSubmit={(e) => {
            e.preventDefault();
            void load(date);
          }}
        >
          <FormField label={t("date")} name="date" type="date" value={date} onChange={(e) => setDate(e.target.value)} required />
          <Button type="submit" busy={busy === "load"} busyLabel={t("working")}>
            {t("show")}
          </Button>
        </form>
      </div>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {done ? <Alert tone="success" title={done} live /> : null}

      <Widget title={t("codeTitle")}>
        <form
          className="booking-row"
          aria-label={t("codeTitle")}
          onSubmit={(e) => {
            e.preventDefault();
            void byCode();
          }}
        >
          <FormField label={t("code")} name="code" value={code} maxLength={12} autoComplete="off" autoCapitalize="characters" spellCheck={false} hint={t("codeHint")}
            onChange={(e) => setCode(e.target.value)} required />
          <Button type="submit" variant="primary" icon="check" busy={busy === "code"} busyLabel={t("working")}>
            {t("checkIn")}
          </Button>
        </form>
      </Widget>

      <Widget title={day.today ? t("todayTitle", { site: pick(day.siteNames, locale) }) : t("dayTitle", { site: pick(day.siteNames, locale) })}>
        <p className="booking-meta" aria-live="polite">
          {t("counts", { waiting, inside, total: day.visits.length })}
        </p>
        {day.visits.length === 0 ? (
          <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="user" />
        ) : (
          <ul className="booking-list" aria-label={t("listLabel")}>
            {day.visits.map((v) => (
              <li key={v.id} className="booking-card">
                <h3>
                  <bdi>{v.name ?? tv("removedName")}</bdi>
                </h3>
                <p className="booking-meta">
                  {v.company ? <><bdi>{v.company}</bdi> · </> : null}
                  {t("host", { name: v.hostName ?? "" })}
                </p>
                <VisitWhen visit={v} />
                <div className="booking-row">
                  <Badge tone={visitTones[v.status]}>{tv(`status.${v.status}`)}</Badge>
                  {v.arrivedAt ? <span className="booking-meta">{t("arrivedAt", { time: localTime(v.arrivedAt, v.timeZone) })}</span> : null}
                  {v.leftAt ? <span className="booking-meta">{t("leftAt", { time: localTime(v.leftAt, v.timeZone) })}</span> : null}
                </div>
                {day.today && (v.status === "expected" || v.status === "arrived") ? (
                  <div className="booking-row">
                    {v.status === "expected" ? (
                      <Button size="sm" variant="secondary" icon="check" busy={busy === v.id} busyLabel={t("working")} onClick={() => void act(v, "check-in")}>
                        {t("checkInName", { name: v.name ?? "" })}
                      </Button>
                    ) : (
                      <Button size="sm" variant="quiet" busy={busy === v.id} busyLabel={t("working")} onClick={() => void act(v, "check-out")}>
                        {t("checkOutName", { name: v.name ?? "" })}
                      </Button>
                    )}
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
