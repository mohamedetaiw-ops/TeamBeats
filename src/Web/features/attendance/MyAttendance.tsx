"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import { currentPosition, thisMonth, type AttendanceMonth, type Punch } from "@/lib/attendance";
import { AttendanceDays, AttendanceTotalsList, MonthPicker } from "./AttendanceMonth";
import { AttendanceRequests } from "./AttendanceRequests";
import "./attendance.css";

/**
 * My attendance (spec 022 US3 scenarios 1 and 2): sign in and out on the web when the tenant allows it, and my month
 * with hours and exceptions. When the tenant turned the location check on, the browser asks for the position for that
 * one sign in; the API decides whether it is inside a site and never keeps the coordinates.
 */
export function MyAttendance() {
  const t = useTranslations("attendance");
  const format = useFormatter();
  const [month, setMonth] = useState(thisMonth());
  const [data, setData] = useState<AttendanceMonth | null>(null);
  const [loadError, setLoadError] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  const [reload, setReload] = useState(0);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`/api/v1/me/attendance?month=${month}`, { cache: "no-store" });
        if (!live) return;
        if (response.ok) {
          setData((await response.json()) as AttendanceMonth);
          setLoadError(null);
        } else {
          setLoadError(await problemMessages(response));
        }
      } catch {
        if (live) setLoadError([t("failed")]);
      }
    })();
    return () => {
      live = false;
    };
  }, [month, reload, t]);

  async function punch(direction: "in" | "out") {
    if (!data) return;
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      let body: Record<string, unknown> = { direction };
      if (data.locationMode !== "off") {
        const position = await currentPosition();
        if (!position) {
          setMessages([t("sign.locationDenied")]);
          return;
        }
        body = { ...body, ...position };
      }
      const response = await fetch("/api/v1/me/attendance/punches", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("sign.failed")]);
        return;
      }
      const created = (await response.json()) as Punch;
      setNotice(t(created.direction === "in" ? (created.remote ? "sign.signedInRemote" : "sign.signedIn") : "sign.signedOut"));
      setReload((n) => n + 1);
    } catch {
      setMessages([t("sign.failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (loadError) return <StateView kind="error" message={loadError.join(" ") || t("failed")} />;
  if (!data) return <StateView kind="loading" />;

  const last = data.lastPunch;
  const signedIn = last?.direction === "in";
  const time = (at: string) => format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" });

  return (
    <div className="attendance">
      <section className="attendance-section" aria-labelledby="attendance-sign">
        <h2 id="attendance-sign">{t("sign.title")}</h2>
        <p className="attendance-status" role="status">
          {last ? t(signedIn ? "sign.statusIn" : "sign.statusOut", { at: time(last.at) }) : t("sign.statusNone")}
        </p>
        {notice ? (
          <p className="notice" role="status">
            {notice}
          </p>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("sign.failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {data.webSignIn ? (
          <>
            {data.locationMode !== "off" ? <p className="field-hint">{t("sign.locationHint")}</p> : null}
            <div className="ui-control-row">
              <Button variant={signedIn ? "secondary" : "primary"} busy={busy && !signedIn} busyLabel={t("sign.saving")} disabled={busy} onClick={() => void punch("in")}>
                {t("sign.in")}
              </Button>
              <Button variant={signedIn ? "primary" : "secondary"} busy={busy && signedIn} busyLabel={t("sign.saving")} disabled={busy} onClick={() => void punch("out")}>
                {t("sign.out")}
              </Button>
            </div>
          </>
        ) : (
          <p className="field-hint">{t("sign.webOff")}</p>
        )}
      </section>
      <section className="attendance-section" aria-labelledby="attendance-summary">
        <h2 id="attendance-summary">{t("summary")}</h2>
        <MonthPicker month={month} onChange={setMonth} />
        <AttendanceTotalsList totals={data.totals} />
      </section>
      <AttendanceDays data={data} />
      <AttendanceRequests onChanged={() => setReload((n) => n + 1)} />
    </div>
  );
}
