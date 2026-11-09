"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import type { MyFeedback } from "@/lib/documents/types";
import { Button } from "@/components/ui/Button";
import "./documents.css";

/** "Was this helpful?" (spec 011 US4 scenario 2): yes or no with an optional comment; a new answer replaces the last one. */
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
    <form
      className="documents-card documents-feedback"
      onSubmit={(e) => void send(e)}
      noValidate
    >
      <fieldset>
        <legend>{t("question")}</legend>
        <label className="documents-check">
          <input
            type="radio"
            name={`${ids}-helpful`}
            checked={helpful === true}
            onChange={() => setHelpful(true)}
          />
          {t("yes")}
        </label>
        <label className="documents-check">
          <input
            type="radio"
            name={`${ids}-helpful`}
            checked={helpful === false}
            onChange={() => setHelpful(false)}
          />
          {t("no")}
        </label>
      </fieldset>
      <div className="field">
        <label htmlFor={`${ids}-comment`}>{t("comment")}</label>
        <textarea
          id={`${ids}-comment`}
          value={comment}
          onChange={(e) => setComment(e.target.value)}
          maxLength={1000}
          rows={3}
          dir="auto"
        />
      </div>
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
      <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
        {mine ? t("update") : t("send")}
      </Button>
    </form>
  );
}
