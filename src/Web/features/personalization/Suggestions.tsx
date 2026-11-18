"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";

type Suggestion = { key: string; kind: "pin_page" | "add_component"; count: number; label: string; href: string | null };
type Answered = { key: string; label: string; state: "accepted" | "undone" | "failed"; messages?: string[] };

/**
 * Safe suggestions for this page (spec 008 FR-035): each says why it is offered and changes nothing until the person
 * accepts it. An accepted one can be undone right here; a declined or undone one is not offered again. Which suggestions
 * are offered, and whether the person may act on them, is decided by the API.
 */
export function Suggestions({ pageId }: { pageId: string }) {
  const t = useTranslations("personalization.suggestions");
  const router = useRouter();
  const [items, setItems] = useState<Suggestion[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [answered, setAnswered] = useState<Answered | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetch(`/api/v1/me/suggestions?pageId=${encodeURIComponent(pageId)}`, { cache: "no-store", signal: controller.signal })
      .then(async (response) => (response.ok ? ((await response.json()) as Suggestion[]) : []))
      .then(setItems, () => undefined);
    return () => controller.abort();
  }, [pageId]);

  async function answer(item: Suggestion, action: "accept" | "decline" | "undo") {
    setBusy(item.key);
    try {
      const response = await fetch(`/api/v1/me/suggestions/${action}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ key: item.key, pageId }),
      });
      if (!response.ok) {
        setAnswered({ key: item.key, label: item.label, state: "failed", messages: await problemMessages(response) });
        return;
      }
      setItems((list) => list.filter((i) => i.key !== item.key));
      setAnswered(action === "decline" ? null : { key: item.key, label: item.label, state: action === "accept" ? "accepted" : "undone" });
      router.refresh();
    } catch {
      setAnswered({ key: item.key, label: item.label, state: "failed", messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  if (items.length === 0 && !answered) return null;

  return (
    <div className="pz-suggestions">
      {items.map((item) => (
        <Alert
          key={item.key}
          tone="info"
          title={item.kind === "pin_page" ? t("pinTitle", { label: item.label }) : t("addTitle", { label: item.label })}
          action={
            <span className="pz-suggestion-actions">
              <Button variant="primary" size="sm" busy={busy === item.key} busyLabel={t("working")} onClick={() => void answer(item, "accept")}>
                {item.kind === "pin_page" ? t("pin") : t("add")}
              </Button>
              <Button variant="quiet" size="sm" disabled={busy === item.key} onClick={() => void answer(item, "decline")}>
                {t("decline")}
              </Button>
            </span>
          }
        >
          <p>{t(`reasons.${item.kind}`, { count: item.count })}</p>
        </Alert>
      ))}
      {answered ? (
        <Alert
          tone={answered.state === "failed" ? "danger" : "success"}
          live
          title={answered.state === "accepted" ? t("accepted", { label: answered.label }) : answered.state === "undone" ? t("undone", { label: answered.label }) : t("failed")}
          action={
            answered.state === "accepted" ? (
              <Button variant="quiet" size="sm" busy={busy === answered.key} busyLabel={t("working")} onClick={() => void answer({ key: answered.key, label: answered.label, kind: "pin_page", count: 0, href: null }, "undo")}>
                {t("undo")}
              </Button>
            ) : (
              <Button variant="quiet" size="sm" onClick={() => setAnswered(null)}>
                {t("dismiss")}
              </Button>
            )
          }
        >
          {answered.messages?.length ? (
            <ul>
              {answered.messages.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          ) : null}
        </Alert>
      ) : null}
    </div>
  );
}
