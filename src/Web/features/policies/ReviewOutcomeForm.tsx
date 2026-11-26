"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { reviewsHref, type ReviewsView, type ReviewView } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { TextAreaField } from "@/components/ui/FormField";
import "./policies.css";

const outcomes = ["no_change", "minor_update", "major_revision", "retire"] as const;

/**
 * The open review of a policy (spec 025 US5 scenarios 1 to 3): one of four outcomes with an optional note. The API
 * records it with the reviewer and date, moves the review date, starts a draft or retires the policy.
 */
export function ReviewOutcomeForm({ policyId, review, rights }: { policyId: string; review: ReviewView; rights: ReviewsView["rights"] }) {
  const t = useTranslations("policies.reviews");
  const format = useFormatter();
  const ids = useId();
  const router = useRouter();
  const [outcome, setOutcome] = useState<string>("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!outcome) {
      setMessages([t("chooseFirst")]);
      return;
    }
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/reviews/${encodeURIComponent(review.id)}/complete`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ outcome, note: note.trim() || null }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      // The page shows what was recorded and the history from the server.
      router.replace(`${reviewsHref(policyId)}?recorded=${encodeURIComponent(outcome)}`);
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const options = outcomes.map((o) => ({
    value: o,
    label: t(`outcomes.${o}`),
    hint: t(o === "retire" && !rights.retire ? "hints.retireAsk" : `hints.${o}`),
    disabled: (o === "minor_update" || o === "major_revision") && !rights.revise,
  }));
  return (
    <form className="policies-card" aria-labelledby={`${ids}-title`} onSubmit={(e) => void submit(e)} noValidate data-testid="review-open">
      <h2 id={`${ids}-title`}>{t("open")}</h2>
      <p className={review.overdueAt ? "field-error" : "policies-meta"}>
        {t(review.overdueAt ? "overdueSince" : "dueOn", { date: format.dateTime(new Date(review.dueAt), { dateStyle: "long" }), number: review.version })}
      </p>
      {review.cause ? (
        <p data-testid="review-cause">
          {t("cause")}{" "}
          {review.cause.link ? (
            <a href={review.cause.link} target="_blank" rel="noopener noreferrer">
              <bdi>{review.cause.title}</bdi>
            </a>
          ) : (
            <bdi>{review.cause.title}</bdi>
          )}
        </p>
      ) : null}
      {rights.complete ? (
        <>
          <RadioGroup legend={t("outcome")} name="reviewOutcome" options={options} value={outcome} onChange={(e) => setOutcome(e.target.value)} cards required />
          <TextAreaField label={t("note")} name="reviewNote" value={note} maxLength={2000} onChange={(e) => setNote(e.target.value)} hint={t("noteHint")} />
          {messages.length > 0 ? (
            <div className="notice notice-error" role="alert">
              {messages.map((m) => (
                <p key={m}>{m}</p>
              ))}
            </div>
          ) : null}
          <div className="button-row">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
              {t("submit")}
            </Button>
          </div>
        </>
      ) : null}
    </form>
  );
}
