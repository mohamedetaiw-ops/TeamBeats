"use client";

import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { CAPTURE_HOURS, type CaptureView } from "@/lib/integrations/types";
import { Messages } from "./parts";
import { send } from "./request";

/**
 * Payload capture for debugging (spec 017 FR-007): off by default. An administrator turns it on for 1 to 24 hours and
 * confirms the connection carries Public or Internal data only; while it is on, each run keeps what was sent and
 * received for 24 hours. The API checks the permission, step-up and every rule.
 */
export function CapturePanel({ connectionId, initial }: { connectionId: string; initial: CaptureView }) {
  const t = useTranslations("connections.capture");
  const format = useFormatter();
  const hoursId = useId();
  const confirmId = useId();
  const [until, setUntil] = useState(initial.until);
  const [hours, setHours] = useState<number>(1);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const path = `/api/v1/integrations/connections/${connectionId}/capture`;
  const returnTo = `/admin/connections/${connectionId}`;

  async function start() {
    setBusy(true);
    const result = await send<CaptureView>("PUT", path, { hours, publicOrInternal: confirmed }, returnTo, t("failed"));
    setBusy(false);
    if (result.ok) {
      setUntil(result.data.until);
      setConfirmed(false);
      setMessages({ ok: true, list: [t("started")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  async function stop() {
    setBusy(true);
    const result = await send<null>("DELETE", path, undefined, returnTo, t("failed"));
    setBusy(false);
    if (result.ok) {
      setUntil(null);
      setMessages({ ok: true, list: [t("stopped")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  return (
    <div className="connections-form" data-testid="payload-capture">
      <h3>{t("title")}</h3>
      <p className="field-hint">{t("intro")}</p>
      {until ? (
        <>
          <p className="notice">{t("on", { until: format.dateTime(new Date(until), { dateStyle: "medium", timeStyle: "short" }) })}</p>
          <div className="connections-row">
            <Button busy={busy} busyLabel={t("working")} onClick={() => void stop()}>
              {t("stop")}
            </Button>
          </div>
        </>
      ) : (
        <>
          <div className="field">
            <label htmlFor={hoursId}>{t("hours")}</label>
            <select id={hoursId} value={hours} onChange={(e) => setHours(Number(e.target.value))}>
              {CAPTURE_HOURS.map((h) => (
                <option key={h} value={h}>
                  {t("hoursOption", { count: h })}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor={confirmId} className="connections-row">
              <input id={confirmId} type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
              {t("confirm")}
            </label>
          </div>
          <div className="connections-row">
            <Button busy={busy} busyLabel={t("working")} disabled={!confirmed} onClick={() => void start()}>
              {t("start")}
            </Button>
          </div>
        </>
      )}
      <Messages messages={messages.list} ok={messages.ok} />
    </div>
  );
}
