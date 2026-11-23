"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { Button } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { FormField, TextAreaField } from "@/components/ui/FormField";

/**
 * Pauses one person's attestation deadline while they are on long leave (spec 025 edge cases): the last day away and a
 * reason kept with compliance. The API checks the dates, the reason and who may pause, and moves the deadline.
 */
export function PauseDeadline({ policyId, campaignId, userId, name }: { policyId: string; campaignId: string; userId: string; name: string }) {
  const t = useTranslations("policies.compliance");
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [until, setUntil] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const formId = `pause-${userId}`;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(
        `/api/v1/policies/${encodeURIComponent(policyId)}/campaigns/${encodeURIComponent(campaignId)}/members/${encodeURIComponent(userId)}/pause`,
        { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ until: until || null, reason: reason.trim() }) },
      );
      if (!response.ok) {
        const problems = await problemMessages(response);
        setMessages(problems.length > 0 ? problems : [t("pauseFailed")]);
        return;
      }
      setOpen(false);
      setUntil("");
      setReason("");
      router.refresh();
    } catch {
      setMessages([t("pauseFailed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)} data-testid="pause-deadline">
        {t("pause")}
      </Button>
      <Dialog
        open={open}
        onClose={() => setOpen(false)}
        title={t("pauseTitle", { name })}
        description={t("pauseIntro")}
        footer={
          <>
            <Button onClick={() => setOpen(false)}>{t("pauseCancel")}</Button>
            <Button type="submit" form={formId} variant="primary" busy={busy} busyLabel={t("pauseBusy")}>
              {t("pauseSubmit")}
            </Button>
          </>
        }
      >
        <form id={formId} onSubmit={(e) => void submit(e)} noValidate>
          <FormField label={t("pauseUntil")} name="pauseUntil" type="date" value={until} required onChange={(e) => setUntil(e.target.value)} hint={t("pauseUntilHint")} />
          <TextAreaField label={t("pauseReason")} name="pauseReason" value={reason} maxLength={500} required onChange={(e) => setReason(e.target.value)} hint={t("pauseReasonHint")} />
          {messages.length > 0 ? (
            <div className="notice notice-error" role="alert">
              {messages.map((m) => (
                <p key={m}>{m}</p>
              ))}
            </div>
          ) : null}
        </form>
      </Dialog>
    </>
  );
}
