"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";

/**
 * Approve, reject or return one task (spec 016 US3 scenario 2). A reason is required to reject; the API checks the
 * assignment, delegation and permission again and refuses a task that is already decided.
 */
export function TaskDecisionPanel({ taskId, onDone, title }: { taskId: string; onDone: (outcome: string) => void; title?: string }) {
  const t = useTranslations("services.decision");
  const id = useId();
  const [comment, setComment] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function decide(outcome: "approve" | "reject" | "return") {
    if (outcome === "reject" && !comment.trim()) {
      setMessages([t("reasonRequired")]);
      return;
    }
    setBusy(outcome);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/tasks/${taskId}/decision`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ outcome, comment: comment.trim() || null }),
      });
      if (response.ok) {
        onDone(outcome);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="services-section" aria-labelledby={`${id}-title`}>
      <h2 id={`${id}-title`}>{title ?? t("title")}</h2>
      <div className="field">
        <label htmlFor={`${id}-comment`}>{t("comment")}</label>
        <p id={`${id}-hint`} className="field-hint">
          {t("commentHint")}
        </p>
        <textarea id={`${id}-comment`} aria-describedby={`${id}-hint`} rows={3} maxLength={2000} value={comment} onChange={(e) => setComment(e.target.value)} />
      </div>
      <div aria-live="polite">
        {messages.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>
      <div className="services-actions">
        <Button variant="primary" busy={busy === "approve"} busyLabel={t("working")} disabled={busy !== null && busy !== "approve"} onClick={() => void decide("approve")}>
          {t("approve")}
        </Button>
        <Button busy={busy === "return"} busyLabel={t("working")} disabled={busy !== null && busy !== "return"} onClick={() => void decide("return")}>
          {t("return")}
        </Button>
        <Button variant="destructive" busy={busy === "reject"} busyLabel={t("working")} disabled={busy !== null && busy !== "reject"} onClick={() => void decide("reject")}>
          {t("reject")}
        </Button>
      </div>
    </section>
  );
}
