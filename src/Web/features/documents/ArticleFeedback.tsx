"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import type { MyFeedback } from "@/lib/documents/types";
import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import "./documents.css";

/**
 * "Was this helpful?" (spec 011 US4 scenario 2; spec 036 DLS-049): two toggle buttons at the end of the article; the
 * comment box and Send appear once the reader chooses. A new answer replaces the last one.
 */
export function ArticleFeedback({
  articleId,
  initial,
}: {
  articleId: string;
  initial: MyFeedback | null;
}) {
  const t = useTranslations("documents.feedback");
  const ids = useId();
  const [mine, setMine] = useState(initial);
  const [helpful, setHelpful] = useState<boolean | null>(
    initial?.helpful ?? null,
  );
  const [comment, setComment] = useState(initial?.comment ?? "");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function send(e: React.FormEvent) {
    e.preventDefault();
    if (helpful === null) {
      setMessages([t("choose")]);
      return;
    }
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(
        `/api/v1/kb/articles/${encodeURIComponent(articleId)}/feedback`,
        {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ helpful, comment: comment.trim() || null }),
        },
      );
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      setMine((await response.json()) as MyFeedback);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="kb-feedback" onSubmit={(e) => void send(e)} noValidate>
      <div className="kb-feedback-question" role="group" aria-labelledby={`${ids}-question`}>
        <span id={`${ids}-question`} className="kb-feedback-label">
          {t("question")}
        </span>
        <button type="button" className="button button-secondary kb-feedback-choice" aria-pressed={helpful === true} onClick={() => setHelpful(true)}>
          <Icon name="check" size={14} />
          {t("yes")}
        </button>
        <button type="button" className="button button-secondary kb-feedback-choice" aria-pressed={helpful === false} onClick={() => setHelpful(false)}>
          <Icon name="close" size={14} />
          {t("no")}
        </button>
      </div>
      {helpful !== null || mine ? (
        <>
          <div className="field">
            <label htmlFor={`${ids}-comment`}>{t("comment")}</label>
            <textarea id={`${ids}-comment`} value={comment} onChange={(e) => setComment(e.target.value)} maxLength={1000} rows={3} dir="auto" />
          </div>
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
            {mine ? t("update") : t("send")}
          </Button>
        </>
      ) : null}
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {mine ? (
        <p className="notice" role="status" data-testid="feedback-saved">
          {t("thanks")}
        </p>
      ) : null}
    </form>
  );
}
