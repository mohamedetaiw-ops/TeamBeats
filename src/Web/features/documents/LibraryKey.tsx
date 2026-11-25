"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { Widget } from "@/components/ui/Widget";
import { problemMessages } from "@/lib/notifications/types";
import type { LibraryEncryptionView } from "@/lib/documents/types";

/**
 * Customer-managed key of a library (spec 011 R3): its managers move the library's files onto the tenant's key once
 * the tenant has connected one. It cannot be undone, so the action asks first. The API decides whether it is allowed.
 */
export function LibraryKey({ libraryId, initial }: { libraryId: string; initial: LibraryEncryptionView }) {
  const t = useTranslations("documents.libraryKey");
  const format = useFormatter();
  const router = useRouter();
  const [view, setView] = useState(initial);
  const [messages, setMessages] = useState<string[]>([]);

  async function turnOn() {
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/documents/libraries/${encodeURIComponent(libraryId)}/encryption`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ customerKey: true }),
      });
      if (response.ok) {
        setView((await response.json()) as LibraryEncryptionView);
        router.refresh();
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    }
  }

  return (
    <Widget title={t("title")}>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("refused")} live>
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </Alert>
      ) : null}
      {view.customerKey ? (
        <>
          <p>
            <Badge tone="success">{t("on")}</Badge>
          </p>
          <p data-testid="library-key-status">
            {view.since ? t("since", { date: format.dateTime(new Date(view.since), { dateStyle: "medium" }) }) : null}{" "}
            {view.pending > 0 ? t("pending", { count: view.pending }) : t("done")}
          </p>
        </>
      ) : view.available ? (
        <>
          <p>{t("intro")}</p>
          <ConfirmAction label={t("turnOn")} question={t("question")} confirmLabel={t("confirm")} cancelLabel={t("cancel")} onConfirm={turnOn} />
        </>
      ) : (
        <p data-testid="library-key-status">{t("unavailable")}</p>
      )}
    </Widget>
  );
}
