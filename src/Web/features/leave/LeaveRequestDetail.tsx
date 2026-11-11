"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { HR_LEAVE_STATES, statusTone, type LeaveRequest } from "@/lib/leave";
import "./leave.css";

/**
 * One leave request (spec 022 US1): dates, days, status, replacement and who else is away. The actions shown are the
 * ones the API says this person may take (`rights`); the API checks each one again. With a task from the inbox, the
 * approver decides here.
 */
export function LeaveRequestDetail({ initial, taskId }: { initial: LeaveRequest; taskId: string | null }) {
  const t = useTranslations("leave");
  const locale = useLocale();
  const format = useFormatter();
  const [request, setRequest] = useState(initial);
  const [task, setTask] = useState(taskId);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [shortenEnd, setShortenEnd] = useState(initial.end);
  const [shortenHalf, setShortenHalf] = useState(false);
  const [replacement, setReplacement] = useState<string | null>(initial.replacement?.id ?? null);

  const date = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "long", timeZone: "UTC" });
  const number = (n: number) => format.number(n, { maximumFractionDigits: 2 });

  async function act(name: string, path: string, body: unknown, method = "POST") {
    setBusy(name);
    setMessages([]);
    setNotice(null);
    try {
      const response = await fetch(`/api/v1/hr/leave/requests/${request.id}${path}`, {
        method,
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body ?? {}),
      });
      if (response.ok) {
        setRequest((await response.json()) as LeaveRequest);
        setNotice(t(`detail.done.${name}`));
      } else {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  async function reload(outcome: string) {
    setTask(null);
    setNotice(outcome === "approve" || outcome === "reject" || outcome === "return" ? t(`detail.decided.${outcome}`) : t("detail.decided.other"));
    try {
      const response = await fetch(`/api/v1/hr/leave/requests/${request.id}`, { cache: "no-store" });
      if (response.ok) setRequest((await response.json()) as LeaveRequest);
    } catch {
      // The decision stands; only the refreshed view is missing.
    }
  }

  const { rights } = request;
  return (
    <div className="leave">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      <section className="leave-section" aria-labelledby="leave-detail">
        <h2 id="leave-detail">
          <bdi>{pick(request.typeNames, locale, request.typeKey)}</bdi> <Badge tone={statusTone[request.status]}>{t(`status.${request.status}`)}</Badge>
        </h2>
        <dl className="leave-details">
          <dt>{t("detail.person")}</dt>
          <dd>
            <bdi>{request.person.displayName}</bdi>
            {request.filedBy.id !== request.person.id ? <span className="field-hint"> {t("detail.filedBy", { name: request.filedBy.displayName })}</span> : null}
          </dd>
          <dt>{t("detail.dates")}</dt>
          <dd>
            {request.start === request.end ? date(request.start) : t("range", { from: date(request.start), to: date(request.end) })}
            {request.startHalf ? <span className="field-hint"> {t("detail.halfStart")}</span> : null}
            {request.endHalf ? <span className="field-hint"> {t("detail.halfEnd")}</span> : null}
          </dd>
          <dt>{t("detail.days")}</dt>
          <dd>{t("form.takes", { days: number(request.days), unit: request.unit })}</dd>
          {request.hrState && (HR_LEAVE_STATES as readonly string[]).includes(request.hrState) ? (
            <>
              <dt>{t("detail.hr")}</dt>
              <dd>
                {t(`hrState.${request.hrState}`)}
                {request.hrReference ? (
                  <span className="field-hint">
                    {" "}
                    <bdi dir="ltr">{request.hrReference}</bdi>
                  </span>
                ) : null}
              </dd>
            </>
          ) : null}
          <dt>{t("detail.replacement")}</dt>
          <dd>
            {request.replacement ? (
              <>
                <bdi>{request.replacement.displayName}</bdi>{" "}
                <span className="field-hint">{request.replacementAnswer ? t(`replacement.${request.replacementAnswer}`) : t("replacement.waiting")}</span>
              </>
            ) : (
              t("detail.none")
            )}
          </dd>
          {request.comment ? (
            <>
              <dt>{t("detail.comment")}</dt>
              <dd>
                <bdi>{request.comment}</bdi>
              </dd>
            </>
          ) : null}
          {request.hasDocument ? (
            <>
              <dt>{t("detail.document")}</dt>
              <dd>
                <a href={`/api/v1/hr/leave/requests/${request.id}/document`}>{t("detail.openDocument")}</a>
              </dd>
            </>
          ) : null}
          {request.changeKind ? (
            <>
              <dt>{t("detail.change")}</dt>
              <dd>{request.changeKind === "shorten" && request.changeEnd ? t("detail.changeShorten", { end: date(request.changeEnd) }) : t("detail.changeCancel")}</dd>
            </>
          ) : null}
        </dl>
      </section>

      {request.overlap ? (
        <section className="leave-section" aria-labelledby="leave-overlap">
          <h2 id="leave-overlap">{t("form.overlap")}</h2>
          {request.overlap.length === 0 ? (
            <p className="field-hint">{t("form.noOverlap")}</p>
          ) : (
            <ul className="leave-overlap">
              {request.overlap.map((o) => (
                <li key={o.userId}>
                  <bdi>{o.displayName}</bdi>{" "}
                  <span className="field-hint">{t("form.overlapDates", { count: o.dates.length, first: date(o.dates[0] ?? request.start), status: o.status })}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}

      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}

      {task && rights.decide ? <TaskDecisionPanel taskId={task} title={t("detail.decide")} onDone={(o) => void reload(o)} /> : null}

      {rights.answerReplacement ? (
        <section className="leave-section" aria-labelledby="leave-answer">
          <h2 id="leave-answer">{t("replacement.title")}</h2>
          <div className="leave-actions">
            <Button variant="primary" busy={busy === "accept"} onClick={() => void act("accept", "/replacement:answer", { accept: true })}>
              {t("replacement.accept")}
            </Button>
            <Button busy={busy === "decline"} onClick={() => void act("decline", "/replacement:answer", { accept: false })}>
              {t("replacement.decline")}
            </Button>
          </div>
        </section>
      ) : null}

      {rights.nameReplacement ? (
        <section className="leave-section" aria-labelledby="leave-name">
          <h2 id="leave-name">{t("detail.nameReplacement")}</h2>
          <PersonPicker label={t("form.replacement")} value={replacement} onChange={setReplacement} />
          <div className="leave-actions">
            <Button busy={busy === "replacement"} onClick={() => void act("replacement", "/replacement", { userId: replacement }, "PUT")}>
              {t("detail.saveReplacement")}
            </Button>
          </div>
        </section>
      ) : null}

      {rights.shorten ? (
        <section className="leave-section" aria-labelledby="leave-shorten">
          <h2 id="leave-shorten">{t("detail.shorten")}</h2>
          <p className="field-hint">{t("detail.shortenHint")}</p>
          <div className="leave-row">
            <FormField label={t("detail.newEnd")} name="shortenEnd" type="date" min={request.start} max={request.end} value={shortenEnd} onChange={(e) => setShortenEnd(e.target.value)} />
            <Checkbox label={t("form.endHalf")} name="shortenHalf" checked={shortenHalf} onChange={(e) => setShortenHalf(e.target.checked)} />
          </div>
          <div className="leave-actions">
            <Button busy={busy === "shorten"} onClick={() => void act("shorten", ":shorten", { end: shortenEnd, endHalf: shortenHalf })}>
              {t("detail.shortenAction")}
            </Button>
          </div>
        </section>
      ) : null}

      {rights.cancel ? (
        <div className="leave-actions">
          <Button variant="destructive" busy={busy === "cancel"} onClick={() => void act("cancel", ":cancel", {})}>
            {request.status === "pending" ? t("detail.withdraw") : t("detail.cancel")}
          </Button>
        </div>
      ) : null}
    </div>
  );
}
