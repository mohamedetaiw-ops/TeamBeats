"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";

type Summary = { outcome: "summarized" | "policy_blocked" | "unavailable"; summary: string | null; denial: string | null };

/**
 * A summary of the request for the person deciding it (spec 016 R2, T026), written by AI through the gateway on
 * request. It is marked as AI-written, nothing is stored, and the decision stays with the person.
 */
export function RequestSummaryPanel({ requestId }: { requestId: string }) {
  const t = useTranslations("services.summary");
  const id = useId();
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Summary | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function summarize() {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/requests/${requestId}/summary`, { method: "POST" });
      if (response.ok) setResult((await response.json()) as Summary);
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="services-section" aria-labelledby={`${id}-title`} data-testid="request-summary">
      <h2 id={`${id}-title`}>{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      <div className="services-actions">
        <Button busy={busy} busyLabel={t("working")} onClick={() => void summarize()}>
          {result ? t("again") : t("summarize")}
        </Button>
      </div>
      <div aria-live="polite">
        {messages.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
        {result?.outcome === "summarized" ? (
          <div className="notice">
            <p className="field-hint">{t("aiWritten")}</p>
            <p>{result.summary}</p>
          </div>
        ) : result ? (
          <p className="notice">{t(result.outcome)}</p>
        ) : null}
      </div>
    </section>
  );
}
