"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import type { DocumentRetentionView } from "@/lib/records/types";

/**
 * A document's record state on its page (spec 011 R3). Everyone who sees the document sees that it is a record and
 * cannot change. Records managers, for whom the page could read the retention view, also declare it or undeclare it
 * with a reason; the API decides whether that is allowed and says why when it is not.
 */
export function RecordStatus({ documentId, recordAt, retention }: { documentId: string; recordAt: string | null; retention: DocumentRetentionView | null }) {
  const t = useTranslations("records.document");
  const format = useFormatter();
  const router = useRouter();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const base = `/api/v1/records/documents/${encodeURIComponent(documentId)}`;

  async function send(url: string, body?: unknown) {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(url, {
        method: "POST",
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        setReason("");
        router.refresh();
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (!recordAt && !retention) {
    return null;
  }
  return (
    <Card title={t("title")} headingLevel={2}>
      {recordAt ? (
        <Alert tone="info" title={t("isRecord", { date: format.dateTime(new Date(recordAt), { dateStyle: "medium" }) })}>
          <p>{t("isRecordBody")}</p>
        </Alert>
      ) : (
        <p className="field-hint" data-testid="record-status">{t("notRecord")}</p>
      )}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("refused")} live>
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </Alert>
      ) : null}
      {retention && !recordAt ? (
        <div className="ui-control-row">
          <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void send(`${base}/declare`)}>
            {t("declare")}
          </Button>
        </div>
      ) : null}
      {retention && recordAt ? (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            void send(`${base}/undeclare`, { reason });
          }}
        >
          <TextAreaField label={t("reason")} hint={t("reasonHint")} name="undeclare-reason" value={reason} onChange={(e) => setReason(e.target.value)} required maxLength={2000} />
          <div className="ui-control-row">
            <Button type="submit" busy={busy} busyLabel={t("working")}>
              {t("undeclare")}
            </Button>
          </div>
        </form>
      ) : null}
    </Card>
  );
}
