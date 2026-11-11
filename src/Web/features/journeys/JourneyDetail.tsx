"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import type { Journey, JourneyStep } from "@/lib/journeys";
import "./journeys.css";

const stepTone = (s: JourneyStep) => (s.overdue ? "danger" : s.status === "open" ? "info" : s.status === "done" ? "success" : "neutral");

/**
 * One journey (spec 022 US4, US5): its steps with dates, assignees and status. The person, the buddy and anyone with a
 * task see it; a step that waits for the viewer can be marked done here. HR officers of the person's unit move the
 * date, name a buddy and cancel. The API decides every one of these again.
 */
export function JourneyDetail({ initial }: { initial: Journey }) {
  const t = useTranslations("journeys.detail");
  const locale = useLocale();
  const format = useFormatter();
  const [view, setView] = useState(initial);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [anchor, setAnchor] = useState(initial.journey.anchorDate);
  const [buddy, setBuddy] = useState<string | null>(initial.journey.buddy?.id ?? null);
  const [reason, setReason] = useState("");
  const [noShow, setNoShow] = useState(false);
  const j = view.journey;
  const open = j.status === "active";

  const date = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const when = (at: string) => format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" });

  async function reload() {
    const response = await fetch(`/api/v1/hr/journeys/${j.id}`, { cache: "no-store" });
    if (response.ok) setView((await response.json()) as Journey);
  }

  async function act(name: string, call: () => Promise<Response>, done: string) {
    setBusy(name);
    setMessages([]);
    setNotice(null);
    try {
      const response = await call();
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      setNotice(done);
      await reload();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  const json = (method: string, body: unknown): RequestInit => ({ method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) });

  return (
    <div className="journeys">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <section className="journeys-section" aria-labelledby="journey-summary">
        <h2 id="journey-summary">
          <bdi>{pick(j.templateNames, locale, j.templateKey)}</bdi>
        </h2>
        <dl className="journeys-details">
          <dt>{t("person")}</dt>
          <dd>
            <bdi>{j.person.displayName}</bdi>
          </dd>
          <dt>{t("kind")}</dt>
          <dd>{t(`kinds.${j.kind}`)}</dd>
          <dt>{t("anchor")}</dt>
          <dd>{date(j.anchorDate)}</dd>
          <dt>{t("buddy")}</dt>
          <dd>{j.buddy ? <bdi>{j.buddy.displayName}</bdi> : t("noBuddy")}</dd>
          <dt>{t("status")}</dt>
          <dd>
            <Badge tone={j.status === "completed" ? "success" : j.status === "active" ? "info" : "neutral"}>{t(`statuses.${j.status}`)}</Badge>{" "}
            {t("progress", { done: j.done, steps: j.steps })}
          </dd>
          {view.cancelReason ? (
            <>
              <dt>{t("cancelReason")}</dt>
              <dd>
                <bdi>{view.cancelReason}</bdi>
              </dd>
            </>
          ) : null}
        </dl>
      </section>

      <section className="journeys-section" aria-labelledby="journey-steps">
        <h2 id="journey-steps">{t("steps")}</h2>
        <div className="journeys-table">
          <table>
            <caption className="visually-hidden">{t("steps")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("step")}</th>
                <th scope="col">{t("scheduled")}</th>
                <th scope="col">{t("assignee")}</th>
                <th scope="col">{t("status")}</th>
                <th scope="col">{t("action")}</th>
              </tr>
            </thead>
            <tbody>
              {view.steps.map((s) => (
                <tr key={s.key}>
                  <td>
                    <bdi>{pick(s.names, locale, s.key)}</bdi>
                  </td>
                  <td>
                    {date(s.scheduledOn)}
                    {s.dueAt && s.status === "open" ? <span className="field-hint"> {t("dueAt", { at: when(s.dueAt) })}</span> : null}
                  </td>
                  <td>{t(`assignees.${s.assignee}`)}</td>
                  <td>
                    <Badge tone={stepTone(s)}>{s.overdue ? t("overdue") : s.status === "done" && s.outcome === "skipped" ? t("skipped") : t(`stepStatuses.${s.status}`)}</Badge>
                    {s.completedBy && s.completedAt ? (
                      <span className="field-hint">
                        {" "}
                        {t("completedBy", { name: s.completedBy.displayName, at: when(s.completedAt) })}
                      </span>
                    ) : null}
                  </td>
                  <td>
                    {s.myTaskId ? (
                      <Button
                        size="sm"
                        variant="primary"
                        busy={busy === s.key}
                        busyLabel={t("working")}
                        onClick={() => void act(s.key, () => fetch(`/api/v1/tasks/${s.myTaskId}/decision`, json("POST", { outcome: "approve" })), t("stepDone"))}
                      >
                        {t("markDone")}
                      </Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {view.canManage && open ? (
        <section className="journeys-section" aria-labelledby="journey-manage">
          <h2 id="journey-manage">{t("manage")}</h2>
          <form
            className="journeys-form"
            onSubmit={(e) => {
              e.preventDefault();
              void act("anchor", () => fetch(`/api/v1/hr/journeys/${j.id}/anchor`, json("PUT", { anchorDate: anchor })), t("anchorMoved"));
            }}
          >
            <div className="journeys-row">
              <FormField label={t("anchor")} hint={t("anchorHint")} name="journeyAnchor" type="date" required value={anchor} onChange={(e) => setAnchor(e.target.value)} />
              <Button type="submit" busy={busy === "anchor"} busyLabel={t("working")}>
                {t("moveAnchor")}
              </Button>
            </div>
          </form>
          <form
            className="journeys-form"
            onSubmit={(e) => {
              e.preventDefault();
              void act("buddy", () => fetch(`/api/v1/hr/journeys/${j.id}/buddy`, json("PUT", { buddyUserId: buddy })), t("buddySaved"));
            }}
          >
            <PersonPicker label={t("buddy")} value={buddy} onChange={setBuddy} />
            <div className="journeys-row">
              <Button type="submit" busy={busy === "buddy"} busyLabel={t("working")}>
                {t("saveBuddy")}
              </Button>
            </div>
          </form>
          <form
            className="journeys-form"
            onSubmit={(e) => {
              e.preventDefault();
              void act("cancel", () => fetch(`/api/v1/hr/journeys/${j.id}/cancel`, json("POST", { reason, noShow })), t("cancelled"));
            }}
          >
            <TextAreaField label={t("reason")} name="journeyReason" required maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
            {j.kind === "onboarding" ? <Checkbox label={t("noShow")} hint={t("noShowHint")} name="journeyNoShow" checked={noShow} onChange={(e) => setNoShow(e.target.checked)} /> : null}
            <div className="journeys-row">
              <Button type="submit" variant="destructive" busy={busy === "cancel"} busyLabel={t("working")}>
                {t("cancel")}
              </Button>
            </div>
          </form>
        </section>
      ) : null}
    </div>
  );
}
