"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { Button } from "@/components/ui/Button";

/** Reminds the viewer's people who still owe an attestation in the campaign shown (spec 025 US3 scenario 4). */
export function RemindButton({ policyId, campaignId }: { policyId: string; campaignId: string }) {
  const t = useTranslations("policies.compliance");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);

  async function remind() {
    setBusy(true);
    setMessage(null);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/compliance/remind`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ campaignId }),
      });
      if (!response.ok) {
        setMessage({ error: true, text: (await problemMessages(response)).join(" ") || t("remindFailed") });
        return;
      }
      const result = (await response.json()) as { recipients: number };
      setMessage({ error: false, text: t("reminded", { count: result.recipients }) });
    } catch {
      setMessage({ error: true, text: t("remindFailed") });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="button-row">
      <Button busy={busy} busyLabel={t("busy")} onClick={() => void remind()}>
        {t("remind")}
      </Button>
      <div aria-live="polite">
        {message ? (
          <p className={message.error ? "field-error" : "notice"} role={message.error ? "alert" : "status"}>
            {message.text}
          </p>
        ) : null}
      </div>
    </div>
  );
}
