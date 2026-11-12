"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { SelectField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import {
  engagementApi,
  reportReasons,
  send,
  type ContentStatus,
  type HeldReason,
  type ReportReason,
} from "@/lib/engagement";

/**
 * What the author sees on their own post or comment while it waits for a moderator or after one removed it, with the
 * reason the moderator gave (spec 023 US8 scenarios 2 and 3). Readers never get these items from the API.
 */
export function StatusNote({
  status,
  heldReason,
  removedReason,
}: {
  status: ContentStatus;
  heldReason: HeldReason | null;
  removedReason: string | null;
}) {
  const t = useTranslations("engagement.moderation");
  if (status === "held" || status === "hidden") {
    return (
      <Alert
        tone="warning"
        title={heldReason === "reports" ? t("hiddenTitle") : t("heldTitle")}
      >
        {heldReason === "reports" ? t("hiddenText") : t("heldText")}
      </Alert>
    );
  }
  if (status === "removed") {
    return (
      <Alert tone="danger" title={t("removedTitle")}>
        {removedReason ? (
          <>
            {t("removedReason")} <bdi>{removedReason}</bdi>
          </>
        ) : null}
      </Alert>
    );
  }
  return null;
}

/**
 * Reporting a post or comment (FR-018). The answer is the same however many people reported it, so the page only
 * thanks the reporter; moderators see the report in their queue.
 */
export function ReportControl({
  type,
  id,
}: {
  type: "post" | "comment";
  id: string;
}) {
  const t = useTranslations("engagement.moderation");
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState<ReportReason>("inappropriate");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    try {
      const response = await send(
        `${engagementApi}/${type}s/${id}:report`,
        "POST",
        { reason, note: note || null },
      );
      if (response.ok) {
        setDone(true);
        setOpen(false);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (done)
    return (
      <p className="engagement-meta" role="status">
        {t("reported")}
      </p>
    );
  if (!open) {
    return (
      <Button
        size="xs"
        variant="quiet"
        icon="alert"
        onClick={() => setOpen(true)}
      >
        {t("report")}
      </Button>
    );
  }
  return (
    <form
      className="engagement-section"
      onSubmit={(e) => void submit(e)}
      aria-label={type === "post" ? t("reportPost") : t("reportComment")}
    >
      <SelectField
        label={t("reason")}
        name={`reason-${id}`}
        value={reason}
        onChange={(e) => setReason(e.target.value as ReportReason)}
        options={reportReasons.map((r) => ({
          value: r,
          label: t(`reasons.${r}`),
        }))}
      />
      <TextAreaField
        label={t("note")}
        name={`note-${id}`}
        hint={t("noteHint")}
        value={note}
        maxLength={500}
        rows={2}
        dir="auto"
        onChange={(e) => setNote(e.target.value)}
      />
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <div className="engagement-row">
        <Button
          type="submit"
          size="sm"
          variant="primary"
          busy={busy}
          busyLabel={t("working")}
        >
          {t("sendReport")}
        </Button>
        <Button size="sm" variant="quiet" onClick={() => setOpen(false)}>
          {t("cancel")}
        </Button>
      </div>
    </form>
  );
}
