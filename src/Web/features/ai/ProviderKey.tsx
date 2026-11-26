"use client";

import { useState, type FormEvent } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { FactList } from "@/components/ui/Patterns";
import { problemMessages } from "@/lib/notifications/types";
import { useHydrated } from "@/lib/clock";
import type { ProviderKeyView } from "@/lib/ai/types";

type Result = { ok: boolean; messages: string[] } | null;

const URL = "/api/v1/ai/provider-keys/openrouter";

/**
 * The organization's own OpenRouter key (spec 013 R3, FR-020), shown only where the installation offers it. The
 * administrator saves the secret manager reference, tests it, and removes it; whether calls use it is the API's answer.
 * The key itself never reaches this page.
 */
export function ProviderKey({ initial }: { initial: ProviderKeyView }) {
  const t = useTranslations("ai.providerKey");
  const format = useFormatter();
  const hydrated = useHydrated();
  const [view, setView] = useState(initial);
  const [reference, setReference] = useState(initial.secretRef ?? "");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const when = (iso: string) => (hydrated ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "");

  async function send(url: string, method: string, body: unknown, done: (v: ProviderKeyView | null) => string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(url, {
        method,
        headers: body === null ? undefined : { "content-type": "application/json" },
        body: body === null ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        const next = response.status === 204 ? null : ((await response.json()) as ProviderKeyView);
        const fresh = next ?? ((await (await fetch(URL, { cache: "no-store" })).json()) as ProviderKeyView);
        setView(fresh);
        setReference(fresh.secretRef ?? "");
        setResult({ ok: !(method === "POST" && next?.lastTestPassed === false), messages: [done(next)] });
        return;
      }
      setResult({ ok: false, messages: await problemMessages(response) });
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    void send(URL, "PUT", { secretRef: reference.trim() }, () => t("saved"));
  }

  const test = view.lastTestAt
    ? t(view.lastTestPassed ? "passedAt" : "failedAt", { when: when(view.lastTestAt) })
    : t("neverTested");

  return (
    <section className="ai-section" aria-labelledby="ai-provider-key" data-testid="ai-provider-key">
      <h2 id="ai-provider-key">{t("title")}</h2>
      <p>{t("intro")}</p>
      {view.secretRef ? (
        <FactList
          inline
          facts={[
            { key: "status", label: t("status"), value: <Badge tone={view.inUse ? "success" : "neutral"}>{t(view.inUse ? "inUse" : "notInUse")}</Badge> },
            { key: "test", label: t("lastTest"), value: test },
          ]}
        />
      ) : null}
      {view.editable ? (
        <form onSubmit={submit}>
          <FormField
            label={t("secretRef")}
            hint={t("secretRefHint")}
            name="providerKeyRef"
            dir="ltr"
            required
            value={reference}
            onChange={(e) => setReference(e.target.value)}
          />
          <div className="button-row">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
              {t("save")}
            </Button>
            {view.secretRef ? (
              <>
                <Button variant="secondary" disabled={busy} onClick={() => void send(`${URL}/test`, "POST", null, (v) => t(v?.lastTestPassed ? "testPassed" : "testFailed"))}>
                  {t("test")}
                </Button>
                <Button variant="secondary" disabled={busy} onClick={() => void send(URL, "DELETE", null, () => t("removed"))}>
                  {t("remove")}
                </Button>
              </>
            ) : null}
          </div>
        </form>
      ) : (
        <p className="field-hint">{t("readOnly")}</p>
      )}
      <div aria-live="polite">
        {result ? (
          <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>
    </section>
  );
}
