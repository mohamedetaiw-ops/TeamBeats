"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";
import { problemMessages } from "@/lib/notifications/types";
import { requestTone, type AttendanceRequest } from "@/lib/attendance";
import "./attendance.css";

/**
 * One attendance request (spec 022 US3 scenarios 3 and 4). The actions shown are the ones the API says this person may
 * take (`rights`); the API checks each one again. With a task from the inbox, the manager decides here.
 */
export function AttendanceRequestDetail({ initial, taskId }: { initial: AttendanceRequest; taskId: string | null }) {
  const t = useTranslations("attendance");
  const format = useFormatter();
  const [request, setRequest] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function reload(done?: string) {
    const response = await fetch(`/api/v1/hr/attendance/requests/${request.id}`, { cache: "no-store" });
    if (response.ok) setRequest((await response.json()) as AttendanceRequest);
    if (done) setNotice(t("detail.decided"));
  }

  async function withdraw() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/hr/attendance/requests/${request.id}:withdraw`, { method: "POST" });
      if (response.ok) {
        setRequest((await response.json()) as AttendanceRequest);
        setNotice(t("detail.withdrawn"));
      } else {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("requests.failed")]);
      }
    } catch {
      setMessages([t("requests.failed")]);
    } finally {
      setBusy(false);
    }
  }

  const day = format.dateTime(new Date(`${request.date}T00:00:00Z`), { dateStyle: "full", timeZone: "UTC" });
  return (
    <div className="attendance">
      <section className="attendance-section" aria-labelledby="request-summary">
        <h2 id="request-summary">{t(`requests.kinds.${request.kind}`)}</h2>
        {notice ? (
          <p className="notice" role="status">
            {notice}
          </p>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("requests.failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <dl className="attendance-totals">
          <div>
            <dt>{t("detail.person")}</dt>
            <dd>
              <bdi>{request.person.displayName}</bdi>
            </dd>
          </div>
          <div>
            <dt>{t("requests.date")}</dt>
            <dd>{day}</dd>
          </div>
          <div>
            <dt>{t("detail.status")}</dt>
            <dd>
              <Badge tone={requestTone[request.status]}>{t(`requests.statuses.${request.status}`)}</Badge>
            </dd>
          </div>
          {request.days !== null ? (
            <div>
              <dt>{t("detail.days")}</dt>
              <dd>{format.number(request.days, { maximumFractionDigits: 1 })}</dd>
            </div>
          ) : null}
        </dl>
        {request.punches.length > 0 ? (
          <>
            <h3>{t("detail.punches")}</h3>
            <ul className="attendance-requests">
              {request.punches.map((p) => (
                <li key={`${p.time}-${p.direction}`}>
                  <bdi dir="ltr">{p.time}</bdi>
                  <span>{t(p.direction === "in" ? "sign.in" : "sign.out")}</span>
                </li>
              ))}
            </ul>
          </>
        ) : null}
        <h3>{t("requests.reason")}</h3>
        <p className="attendance-reason">{request.reason}</p>
        {request.rights.withdraw ? (
          <div className="attendance-row">
            <Button busy={busy} busyLabel={t("sign.saving")} onClick={() => void withdraw()}>
              {t("detail.withdraw")}
            </Button>
          </div>
        ) : null}
      </section>
      {taskId && request.rights.decide ? <TaskDecisionPanel taskId={taskId} title={t("detail.decide")} onDone={(o) => void reload(o)} /> : null}
    </div>
  );
}
