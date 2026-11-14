"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { unavailableReasons, type TextAction, type TextActionResult } from "@/lib/assistant/types";

type Run = { busy: TextAction | null; result: TextActionResult | null; error: string | null };

export async function runAction(body: Record<string, unknown>): Promise<{ result: TextActionResult | null; error: string | null; status: number }> {
  const response = await fetch("/api/v1/assistant/actions", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    return { result: null, error: (await problemMessages(response)).join(" ") || null, status: response.status };
  }
  return { result: (await response.json()) as TextActionResult, error: null, status: response.status };
}

/** The result of a text action, labelled as AI generated (FR-007), or the message for why there is none. */
export function Result({ result }: { result: TextActionResult }) {
  const t = useTranslations("assistant");
  const [copied, setCopied] = useState(false);
  const reason = unavailableReasons.find((r) => r === result.reason) ?? "other";
  if (result.outcome === "policy_blocked") return <p className="notice">{t("outcomes.policy_blocked")}</p>;
  if (result.outcome !== "answered" || !result.text) {
    return (
      <p className="notice" role="alert">
        {t(`unavailable.${reason}`)}
      </p>
    );
  }
  return (
    <div className="assistant-result" lang={result.language} dir={result.language === "ar" ? "rtl" : "ltr"}>
      <p className="assistant-label">{t("generated")}</p>
      <p className="assistant-text">{result.text}</p>
      {result.withheld ? <p className="assistant-note">{t("withheld")}</p> : null}
      <div className="button-row">
        <Button
          variant="quiet"
          onClick={() =>
            void navigator.clipboard
              ?.writeText(result.text ?? "")
              .then(() => setCopied(true))
              .catch(() => undefined)
          }
        >
          {copied ? t("tools.copied") : t("tools.copy")}
        </Button>
      </div>
    </div>
  );
}

/**
 * Summarize, explain or translate the item on this page (spec 014 US3 scenario 1). The API reads the item as the
 * person, so it works only on what they can read and nothing is stored or published.
 */
export function ItemActions({ source, itemId, itemLocale, uiLocale }: { source: string; itemId: string; itemLocale: string; uiLocale: string }) {
  const t = useTranslations("assistant");
  const [run, setRun] = useState<Run>({ busy: null, result: null, error: null });
  // Translate into the reader's language, or into the other launch language when the item is already in it.
  const target = itemLocale === uiLocale ? (uiLocale === "ar" ? "en" : "ar") : uiLocale;

  async function go(action: TextAction) {
    setRun({ busy: action, result: null, error: null });
    try {
      const { result, error, status } = await runAction({
        action,
        source,
        itemId,
        language: action === "translate" ? target : uiLocale,
      });
      setRun({ busy: null, result, error: result ? null : status === 403 ? t("notAvailable") : (error ?? t("failed")) });
    } catch {
      setRun({ busy: null, result: null, error: t("failed") });
    }
  }

  return (
    <section className="assistant-item-actions" aria-labelledby="assistant-item-actions-title">
      <h2 id="assistant-item-actions-title">{t("tools.itemTitle")}</h2>
      <div className="button-row">
        {(["summarize", "explain", "translate"] as const).map((action) => (
          <Button key={action} busy={run.busy === action} busyLabel={t("thinking")} onClick={() => void go(action)}>
            {action === "translate" ? t("tools.translateTo", { language: t(`tools.languages.${target}`) }) : t(`tools.actions.${action}`)}
          </Button>
        ))}
      </div>
      <div aria-live="polite">
        {run.result ? <Result result={run.result} /> : null}
        {run.error ? (
          <p className="field-error" role="alert">
            {run.error}
          </p>
        ) : null}
      </div>
    </section>
  );
}
