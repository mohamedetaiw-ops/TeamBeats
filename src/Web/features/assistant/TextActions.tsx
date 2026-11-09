"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { unavailableReasons, type TextAction, type TextActionResult } from "@/lib/assistant/types";

type Run = { busy: TextAction | null; result: TextActionResult | null; error: string | null };

async function runAction(body: Record<string, unknown>): Promise<{ result: TextActionResult | null; error: string | null; status: number }> {
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
function Result({ result }: { result: TextActionResult }) {
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

/** Work with the person's own text (spec 014 US3 scenario 2): rewrite, draft, summarize, explain or translate. */
export function TextTools({ uiLocale }: { uiLocale: string }) {
  const t = useTranslations("assistant");
  const [action, setAction] = useState<TextAction>("rewrite");
  const [text, setText] = useState("");
  const [instructions, setInstructions] = useState("");
  const [language, setLanguage] = useState(uiLocale === "ar" ? "en" : "ar");
  const [run, setRun] = useState<Run>({ busy: null, result: null, error: null });
  const ids = { action: useId(), text: useId(), instructions: useId(), language: useId() };

  async function submit(e: FormEvent) {
    e.preventDefault();
    setRun({ busy: action, result: null, error: null });
    try {
      const { result, error, status } = await runAction({
        action,
        text: text.trim() || null,
        instructions: instructions.trim() || null,
        language: action === "translate" ? language : null,
      });
      setRun({ busy: null, result, error: result ? null : status === 403 ? t("notAvailable") : (error ?? t("failed")) });
    } catch {
      setRun({ busy: null, result: null, error: t("failed") });
    }
  }

  return (
    <section className="assistant-tools" aria-labelledby="assistant-tools-title">
      <h2 id="assistant-tools-title">{t("tools.title")}</h2>
      <p>{t("tools.intro")}</p>
      <form className="assistant-tools-form" onSubmit={(e) => void submit(e)}>
        <div className="field">
          <label htmlFor={ids.action}>{t("tools.action")}</label>
          <select id={ids.action} value={action} onChange={(e) => setAction(e.target.value as TextAction)}>
            {(["rewrite", "draft", "summarize", "explain", "translate"] as const).map((a) => (
              <option key={a} value={a}>
                {t(`tools.actions.${a}`)}
              </option>
            ))}
          </select>
        </div>
        {action === "translate" ? (
          <div className="field">
            <label htmlFor={ids.language}>{t("tools.language")}</label>
            <select id={ids.language} value={language} onChange={(e) => setLanguage(e.target.value)}>
              <option value="ar">{t("tools.languages.ar")}</option>
              <option value="en">{t("tools.languages.en")}</option>
            </select>
          </div>
        ) : null}
        <div className="field">
          <label htmlFor={ids.text}>{t("tools.text")}</label>
          <textarea id={ids.text} value={text} rows={6} maxLength={20000} dir="auto" required={action !== "draft"} onChange={(e) => setText(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={ids.instructions}>{action === "draft" ? t("tools.brief") : t("tools.instructions")}</label>
          <input
            id={ids.instructions}
            value={instructions}
            maxLength={500}
            dir="auto"
            required={action === "draft" && text.trim().length === 0}
            onChange={(e) => setInstructions(e.target.value)}
          />
        </div>
        <div className="button-row">
          <Button type="submit" variant="primary" busy={run.busy !== null} busyLabel={t("thinking")}>
            {t("tools.run")}
          </Button>
        </div>
      </form>
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
