"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { AssistantSettings } from "@/lib/assistant/types";

/** The conversation retention of the assistant (spec 014 section 3), set by the AI Policy Administrator. */
export function AssistantRetention({ initial, canManage }: { initial: AssistantSettings; canManage: boolean }) {
  const t = useTranslations("assistant.admin");
  const [days, setDays] = useState(String(initial.retentionDays));
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);
  const id = useId();

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      const response = await fetch("/api/v1/assistant/settings", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ retentionDays: Number.parseInt(days, 10) || null }),
      });
      setMessage(response.ok ? { error: false, text: t("saved") } : { error: true, text: (await problemMessages(response)).join(" ") || t("failed") });
    } catch {
      setMessage({ error: true, text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="ai-section" onSubmit={(e) => void save(e)}>
      <p>{t("intro")}</p>
      <div className="field">
        <label htmlFor={id}>{t("retention")}</label>
        <p id={`${id}-hint`} className="field-hint">
          {t("retentionHint")}
        </p>
        <input
          id={id}
          type="number"
          min={1}
          max={365}
          required
          value={days}
          disabled={!canManage}
          aria-describedby={`${id}-hint`}
          onChange={(e) => setDays(e.target.value)}
        />
      </div>
      {canManage ? (
        <div className="button-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
            {t("save")}
          </Button>
        </div>
      ) : null}
      <div aria-live="polite">
        {message ? (
          <p className={message.error ? "field-error" : "notice"} role={message.error ? "alert" : "status"}>
            {message.text}
          </p>
        ) : null}
      </div>
    </form>
  );
}
