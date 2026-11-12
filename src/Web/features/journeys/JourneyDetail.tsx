"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { EXIT_REASONS, type ExitInterview, type ExitReason, type Journey, type JourneyStep } from "@/lib/journeys";
import "./journeys.css";

const stepTone = (s: JourneyStep) => (s.overdue ? "danger" : s.status === "open" ? "info" : s.status === "done" ? "success" : "neutral");

/**
 * One journey (spec 022 US4, US5): its steps with dates, assignees and status. The person, the buddy and anyone with a
 * task see it; a step that waits for the viewer can be marked done here. HR officers of the person's unit move the
 * date, name a buddy and cancel. An attestation step is done on My policies, an access removal step line by line on its
 * checklist and an exit interview step through its form (T019). The API decides every one of these again.
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
                    {s.type === "preboarding" && s.content ? <p className="journeys-content">{pick(s.content, locale, "")}</p> : null}
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
                    {s.status === "open" && s.type === "attestation" ? (
                      <ButtonLink size="sm" href="/policies/my">
                        {t("openPolicies")}
                      </ButtonLink>
                    ) : null}
                    {s.status === "open" && s.type === "training" ? (
                      <ButtonLink size="sm" href="/learning">
                        {t("openLearning")}
                      </ButtonLink>
                    ) : null}
                    {s.myTaskId && s.type === "access_removal" ? <span className="field-hint">{t("useChecklist")}</span> : null}
                    {s.myTaskId && s.type === "exit_interview" ? <span className="field-hint">{t("useForm")}</span> : null}
                    {s.myTaskId && (s.type === "task" || s.type === "preboarding") ? (
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

      {view.steps
        .filter((s) => s.type === "access_removal" && s.accessItems)
        .map((s) => (
          <section key={s.key} className="journeys-section" aria-labelledby={`journey-access-${s.key}`}>
            <h2 id={`journey-access-${s.key}`}>
              {t("checklist")}: <bdi>{pick(s.names, locale, s.key)}</bdi>
            </h2>
            <p className="field-hint">{t("checklistHint")}</p>
            <ul className="journeys-list">
              {(s.accessItems ?? []).map((item) => (
                <li key={item.id} className="journeys-item">
                  <span>
                    <bdi>{item.name}</bdi>{" "}
                    <Badge tone={item.status === "pending" ? (item.escalatedAt ? "danger" : "warning") : "success"}>{t(`accessStatuses.${item.status}`)}</Badge>
                    {item.escalatedAt && item.status === "pending" ? <span className="field-hint"> {t("escalated")}</span> : null}
                    {item.confirmedBy && item.confirmedAt ? (
                      <span className="field-hint"> {t("completedBy", { name: item.confirmedBy.displayName, at: when(item.confirmedAt) })}</span>
                    ) : null}
                    {item.note ? (
                      <span className="field-hint">
                        {" "}
                        <bdi>{item.note}</bdi>
                      </span>
                    ) : null}
                  </span>
                  {s.myTaskId && s.status === "open" && item.status === "pending" ? (
                    <span className="journeys-row">
                      <Button
                        size="sm"
                        variant="primary"
                        busy={busy === item.id}
                        busyLabel={t("working")}
                        onClick={() =>
                          void act(item.id, () => fetch(`/api/v1/hr/journeys/${j.id}/steps/${s.key}/access-items/${item.id}`, json("POST", { status: "removed" })), t("accessConfirmed"))
                        }
                      >
                        {t("removed")}
                      </Button>
                      <Button
                        size="sm"
                        busy={busy === `${item.id}-na`}
                        busyLabel={t("working")}
                        onClick={() =>
                          void act(`${item.id}-na`, () => fetch(`/api/v1/hr/journeys/${j.id}/steps/${s.key}/access-items/${item.id}`, json("POST", { status: "not_applicable" })), t("accessConfirmed"))
                        }
                      >
                        {t("notApplicable")}
                      </Button>
                    </span>
                  ) : null}
                </li>
              ))}
            </ul>
          </section>
        ))}

      {view.steps
        .filter((s) => s.type === "exit_interview" && s.myTaskId && s.status === "open" && !view.exitInterview)
        .map((s) => (
          <ExitInterviewForm
            key={s.key}
            busy={busy === "exit"}
            submit={(body) => act("exit", () => fetch(`/api/v1/hr/journeys/${j.id}/steps/${s.key}/exit-interview`, json("POST", body)), t("exitRecorded"))}
          />
        ))}

      {view.exitInterview ? <ExitInterviewSummary interview={view.exitInterview} when={when} /> : null}

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

/** The exit interview form (spec 022 US5 scenario 5): structured reasons for analytics, free text read by HR only. */
function ExitInterviewForm({ busy, submit }: { busy: boolean; submit: (body: unknown) => Promise<void> }) {
  const t = useTranslations("journeys.detail");
  const [declined, setDeclined] = useState(false);
  const [reasons, setReasons] = useState<ExitReason[]>([]);
  const [primary, setPrimary] = useState("");
  const [rating, setRating] = useState("");
  const [wouldReturn, setWouldReturn] = useState(false);
  const [comment, setComment] = useState("");
  const toggle = (reason: ExitReason, on: boolean) => setReasons((list) => (on ? [...list, reason] : list.filter((r) => r !== reason)));

  return (
    <section className="journeys-section" aria-labelledby="journey-exit">
      <h2 id="journey-exit">{t("exitTitle")}</h2>
      <p className="field-hint">{t("exitIntro")}</p>
      <form
        className="journeys-form"
        onSubmit={(e) => {
          e.preventDefault();
          void submit(
            declined
              ? { declined: true }
              : { declined: false, reasons, primaryReason: primary || null, rating: rating ? Number(rating) : null, wouldReturn, comment: comment.trim() || null },
          );
        }}
      >
        <Checkbox label={t("exitDeclined")} name="exitDeclined" checked={declined} onChange={(e) => setDeclined(e.target.checked)} />
        {declined ? null : (
          <>
            <fieldset className="journeys-step">
              <legend>{t("exitReasons")}</legend>
              <div className="journeys-reasons">
                {EXIT_REASONS.map((r) => (
                  <Checkbox key={r} label={t(`reasons.${r}`)} name={`exitReason-${r}`} checked={reasons.includes(r)} onChange={(e) => toggle(r, e.target.checked)} />
                ))}
              </div>
            </fieldset>
            <div className="journeys-row">
              <SelectField
                label={t("exitPrimary")}
                name="exitPrimary"
                value={primary}
                onChange={(e) => setPrimary(e.target.value)}
                options={[{ value: "", label: t("exitNoPrimary") }, ...reasons.map((r) => ({ value: r, label: t(`reasons.${r}`) }))]}
              />
              <SelectField
                label={t("exitRating")}
                name="exitRating"
                value={rating}
                onChange={(e) => setRating(e.target.value)}
                options={[{ value: "", label: t("exitNoRating") }, ...[1, 2, 3, 4, 5].map((n) => ({ value: String(n), label: t("exitRatingValue", { value: n }) }))]}
              />
            </div>
            <Checkbox label={t("exitWouldReturn")} name="exitWouldReturn" checked={wouldReturn} onChange={(e) => setWouldReturn(e.target.checked)} />
            <TextAreaField label={t("exitComment")} hint={t("exitCommentHint")} name="exitComment" maxLength={2000} rows={4} value={comment} onChange={(e) => setComment(e.target.value)} />
          </>
        )}
        <div className="journeys-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={!declined && reasons.length === 0}>
            {t("exitSubmit")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function ExitInterviewSummary({ interview, when }: { interview: ExitInterview; when: (at: string) => string }) {
  const t = useTranslations("journeys.detail");
  return (
    <section className="journeys-section" aria-labelledby="journey-exit-record">
      <h2 id="journey-exit-record">{t("exitTitle")}</h2>
      <p className="field-hint">{t("completedBy", { name: interview.submittedBy.displayName, at: when(interview.submittedAt) })}</p>
      {interview.declined ? (
        <p>{t("exitWasDeclined")}</p>
      ) : (
        <dl className="journeys-details">
          <dt>{t("exitReasons")}</dt>
          <dd>{interview.reasons.map((r) => t(`reasons.${r}`)).join(", ")}</dd>
          {interview.primaryReason ? (
            <>
              <dt>{t("exitPrimary")}</dt>
              <dd>{t(`reasons.${interview.primaryReason}`)}</dd>
            </>
          ) : null}
          {interview.rating ? (
            <>
              <dt>{t("exitRating")}</dt>
              <dd>{t("exitRatingValue", { value: interview.rating })}</dd>
            </>
          ) : null}
          {interview.wouldReturn !== null ? (
            <>
              <dt>{t("exitWouldReturn")}</dt>
              <dd>{interview.wouldReturn ? t("yes") : t("no")}</dd>
            </>
          ) : null}
          {interview.comment ? (
            <>
              <dt>{t("exitComment")}</dt>
              <dd>
                <bdi>{interview.comment}</bdi>
              </dd>
            </>
          ) : null}
        </dl>
      )}
    </section>
  );
}
