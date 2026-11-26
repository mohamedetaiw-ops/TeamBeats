"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { send, type ThreadSummaryView } from "@/lib/engagement";

/**
 * Asks the API for a short AI summary of a long thread or a busy room (spec 023 R3 US11, FR-031). The button shows only
 * when the API offered it; the answer, or why there is none, shows under it and is not kept.
 */
export function SummaryControl({ url }: { url: string }) {
  const t = useTranslations("engagement.summary");
  const [busy, setBusy] = useState(false);
  const [summary, setSummary] = useState<ThreadSummaryView | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function ask() {
    setBusy(true);
    setMessages([]);
    setSummary(null);
    try {
      const response = await send(url, "POST");
      if (response.ok) setSummary((await response.json()) as ThreadSummaryView);
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Button size="sm" variant="quiet" icon="spark" busy={busy} busyLabel={t("working")} onClick={() => void ask()}>
        {t("ask")}
      </Button>
      <div className="engagement-summary" aria-live="polite">
      {messages.length > 0 ? (
        <Alert tone="warning" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {summary?.outcome === "summarized" ? (
        <Alert tone="info" title={t("title")} live>
          <span dir="auto">{summary.summary}</span> {t("note")}
        </Alert>
      ) : summary ? (
        <Alert tone="warning" title={t("failed")} live>
          {summary.outcome === "policy_blocked" ? t("blocked") : t("unavailable")}
        </Alert>
      ) : null}
      </div>
    </>
  );
}
