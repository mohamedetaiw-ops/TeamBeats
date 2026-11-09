"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";

/** Milliseconds between refreshes of a run that is still going. */
const REFRESH = 3000;

/** Keeps a queued or running evaluation run current on the page, and cancels it when the API allows (spec 034 US3 scenario 7). */
export function RunLive({ runId, active, canCancel }: { runId: string; active: boolean; canCancel: boolean }) {
  const t = useTranslations("evaluation");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  useEffect(() => {
    if (!active) return;
    const timer = window.setInterval(() => router.refresh(), REFRESH);
    return () => window.clearInterval(timer);
  }, [active, router]);

  async function cancel() {
    setBusy(true);
    setMessages([]);
    const result = await send(`/api/v1/evaluation/runs/${runId}/cancel`, "POST", undefined, t("failed"));
    setBusy(false);
    if (result.ok) router.refresh();
    else setMessages(result.messages);
  }

  if (!canCancel && messages.length === 0) return null;
  return (
    <div className="agents-row">
      {canCancel ? (
        <Button variant="destructive" busy={busy} busyLabel={t("working")} onClick={() => void cancel()}>
          {t("run.cancel")}
        </Button>
      ) : null}
      {messages.length > 0 ? (
        <div role="alert" className="notice notice-error">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}
