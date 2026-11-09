"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { itemAnalyticsApi, type AnalyticsSource } from "@/lib/analytics/types";
import { Button } from "@/components/ui/Button";

/** "Remind this group" (spec 021 US1 scenario 3): the owning module reminds the people in the group who have not read or confirmed. */
export function RemindGroupButton({ source, itemId, group, label }: { source: AnalyticsSource; itemId: string; group: string; label: string }) {
  const t = useTranslations("analytics.item");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);

  async function remind() {
    setBusy(true);
    setMessage(null);
    try {
      const response = await fetch(`${itemAnalyticsApi(source, itemId)}/remind`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ group }),
      });
      if (!response.ok) {
        setMessage({ error: true, text: (await problemMessages(response)).join(" ") || t("remindFailed") });
        return;
      }
      const result = (await response.json()) as { sent: number };
      setMessage({ error: false, text: t("reminded", { count: result.sent }) });
    } catch {
      setMessage({ error: true, text: t("remindFailed") });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="analytics-remind">
      <Button variant="secondary" busy={busy} busyLabel={t("busy")} onClick={() => void remind()} aria-label={t("remindGroup", { group: label })}>
        {t("remind")}
      </Button>
      <span aria-live="polite">
        {message ? (
          <span className={message.error ? "field-error" : "notice"} role={message.error ? "alert" : "status"}>
            {message.text}
          </span>
        ) : null}
      </span>
    </div>
  );
}
