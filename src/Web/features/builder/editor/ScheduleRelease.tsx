"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PageSchedule, PageScheduleState } from "@/lib/experience/builder-types";
import { problemMessages } from "@/lib/notifications/types";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";

/**
 * Scheduled releases in the builder (spec 032 R2 T025, FR-053): the draft goes live, and may end, at times in the tenant's
 * time zone. The server keeps the draft as scheduled, checks it again at the time and refuses what the rules refuse.
 */
export function ScheduleRelease({ pageId, beforeSchedule, disabled }: { pageId: string; beforeSchedule: () => Promise<void>; disabled: boolean }) {
  const t = useTranslations("builder.schedule");
  const locale = useLocale();
  const [schedule, setSchedule] = useState<PageSchedule | null>(null);
  const [zone, setZone] = useState("");
  const [loaded, setLoaded] = useState(false);
  const [goLive, setGoLive] = useState("");
  const [ends, setEnds] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [said, setSaid] = useState("");
  const url = `/api/v1/experience/pages/${pageId}/schedule`;

  useEffect(() => {
    let live = true;
    fetch(url)
      .then(async (r) => (r.ok ? ((await r.json()) as PageScheduleState) : null))
      .catch(() => null)
      .then((state) => {
        if (!live || !state) return;
        setSchedule(state.current);
        setZone(state.timeZone);
        setLoaded(true);
      });
    return () => {
      live = false;
    };
  }, [url]);

  const when = (iso: string, zone: string) =>
    new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone: zone }).format(new Date(iso));
  const waiting = schedule?.status === "scheduled";
  const ending = schedule?.status === "live" && schedule.expiresAt !== null && new Date(schedule.expiresAt) > new Date();

  async function save() {
    setBusy(true);
    setMessages([]);
    try {
      await beforeSchedule();
      const response = await fetch(url, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ goLiveAt: goLive || null, expiresAt: ends || null }),
      });
      if (response.ok) {
        setSchedule((await response.json()) as PageSchedule);
        setGoLive("");
        setEnds("");
        setSaid(t("scheduled"));
        return;
      }
      const problems = await problemMessages(response);
      setMessages(problems.length > 0 ? problems : [t("failed")]);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function cancel() {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(url, { method: "DELETE" });
      if (response.ok) {
        setSchedule(waiting ? { ...schedule!, status: "cancelled" } : { ...schedule!, expiresAt: null });
        setSaid(t(waiting ? "cancelled" : "keptLive"));
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (!loaded) return null;
  return (
    <div className="builder-schedule" data-testid="builder-schedule">
      <h3>{t("title")}</h3>
      {waiting ? (
        <>
          <p className="notice" role="status" data-testid="builder-schedule-waiting">
            {t("waiting", { when: when(schedule.goLiveAt, schedule.timeZone) })}
            {schedule.expiresAt ? ` ${t("until", { when: when(schedule.expiresAt, schedule.timeZone) })}` : ""}
          </p>
          <Button busy={busy} busyLabel={t("working")} onClick={() => void cancel()}>
            {t("cancel")}
          </Button>
        </>
      ) : ending ? (
        <>
          <p className="notice" role="status">
            {t("ending", { when: when(schedule.expiresAt!, schedule.timeZone) })}
          </p>
          <Button busy={busy} busyLabel={t("working")} onClick={() => void cancel()}>
            {t("keepLive")}
          </Button>
        </>
      ) : (
        <>
          {schedule?.status === "failed" ? (
            <p className="notice" role="note" data-testid="builder-schedule-failed">
              {t("didNotGoLive", { when: when(schedule.goLiveAt, schedule.timeZone) })}
            </p>
          ) : null}
          <FormField
            label={t("goLive")}
            name="schedule-go-live"
            type="datetime-local"
            hint={t("zone", { zone })}
            value={goLive}
            onChange={(e) => setGoLive(e.target.value)}
          />
          <FormField label={t("ends")} name="schedule-ends" type="datetime-local" hint={t("endsHint")} value={ends} onChange={(e) => setEnds(e.target.value)} />
          <Button busy={busy} busyLabel={t("working")} disabled={disabled || goLive === ""} onClick={() => void save()}>
            {t("schedule")}
          </Button>
        </>
      )}
      {messages.length > 0 ? (
        <ul className="builder-schedule-errors" role="alert">
          {messages.map((m) => (
            <li key={m}>{m}</li>
          ))}
        </ul>
      ) : null}
      <p className="visually-hidden" aria-live="polite">
        {said}
      </p>
    </div>
  );
}
