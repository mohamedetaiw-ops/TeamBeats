"use client";

import { useState, type FormEvent } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormPanel, TextAreaField } from "@/components/ui/FormField";
import { FactList } from "@/components/ui/Patterns";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { problemMessages } from "@/lib/notifications/types";
import { EXTERNAL_TASK_OUTCOMES, type ExternalTask } from "@/lib/processes";
import "./processes.css";

const TONES: Record<ExternalTask["status"], BadgeTone> = { open: "info", completing: "accent", completed: "success", failed: "danger", closed: "neutral" };

/**
 * Completes a Camunda 8 user task from TeamBeats (spec 024 US8 scenario 2). The person picks how it ended and may add a
 * comment; the API sends both to Camunda, shows a failure here and retries it. Whether it can still be completed is
 * the API's answer, shown through the status it returns.
 */
export function ExternalTaskForm({ initial }: { initial: ExternalTask }) {
  const t = useTranslations("processes.externalTask");
  const format = useFormatter();
  const [task, setTask] = useState(initial);
  const [outcome, setOutcome] = useState(initial.outcome ?? "");
  const [comment, setComment] = useState(initial.comment ?? "");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium", timeStyle: "short" });
  const canComplete = task.status === "open" || task.status === "failed";

  async function complete(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/workflow/external-tasks/${encodeURIComponent(task.id)}:complete`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ outcome: outcome || null, comment: comment || null }),
      });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      setTask((await response.json()) as ExternalTask);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const facts = [
    { key: "status", label: t("status"), value: <Badge tone={TONES[task.status]}>{t(`statuses.${task.status}`)}</Badge> },
    { key: "created", label: t("created"), value: when(task.createdAt) },
    ...(task.dueAt ? [{ key: "due", label: t("due"), value: when(task.dueAt) }] : []),
    ...(task.completedAt ? [{ key: "completed", label: t("completedAt"), value: when(task.completedAt) }] : []),
  ];

  return (
    <div className="processes-external">
      <FactList facts={facts} />
      {task.status === "failed" ? (
        <Alert tone="danger" title={t("sendFailed")}>
          {task.nextAttemptAt ? t("retrying", { when: when(task.nextAttemptAt) }) : t("noRetry")}
        </Alert>
      ) : null}
      {task.status === "completing" ? <Alert tone="info" title={t("sending")} /> : null}
      {task.status === "completed" ? <Alert tone="success" title={t("done")} live /> : null}
      {task.status === "closed" ? <Alert tone="warning" title={t("closed")} /> : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {canComplete ? (
        <FormPanel as="form" onSubmit={(e) => void complete(e)} aria-labelledby="external-task-form">
          <h2 id="external-task-form">{t("formTitle")}</h2>
          <RadioGroup
            legend={t("outcome")}
            name="outcome"
            required
            value={outcome}
            onChange={(e) => setOutcome(e.target.value)}
            options={EXTERNAL_TASK_OUTCOMES.map((o) => ({ value: o, label: t(`outcomes.${o}`) }))}
          />
          <TextAreaField label={t("comment")} name="comment" maxLength={2000} value={comment} onChange={(e) => setComment(e.target.value)} hint={t("commentHint")} />
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={!outcome}>
            {task.status === "failed" ? t("tryAgain") : t("complete")}
          </Button>
        </FormPanel>
      ) : null}
    </div>
  );
}
