"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import type { CatalogueEntry, ModelPolicyView, PolicySaveResult } from "@/lib/ai/types";

type Result = { ok: boolean; messages: string[] } | null;

export function modelName(entry: CatalogueEntry | undefined, model: string, locale: string): string {
  return entry?.names[locale] ?? entry?.names.en ?? model;
}

/**
 * Model policy (spec 013 US1): the platform catalogue with each model's provider, regions, retention terms, highest
 * classification and price, the tenant's allowlist and Confidential models, and parameter limits. The API decides what
 * may be saved and says why the page is read-only; a change to a gated route comes back held for the release gate.
 */
export function PolicyEditor({ catalogue, initial }: { catalogue: CatalogueEntry[]; initial: ModelPolicyView }) {
  const t = useTranslations("ai.policy");
  const locale = useLocale();
  const format = useFormatter();
  const [view, setView] = useState(initial);
  const [allow, setAllow] = useState(new Set(initial.policy.allowlist));
  const [confidential, setConfidential] = useState(new Set(initial.policy.confidential));
  const [maxTokens, setMaxTokens] = useState(initial.policy.maxOutputTokens === null ? "" : String(initial.policy.maxOutputTokens));
  const [minTemp, setMinTemp] = useState(initial.policy.minTemperature === null ? "" : String(initial.policy.minTemperature));
  const [maxTemp, setMaxTemp] = useState(initial.policy.maxTemperature === null ? "" : String(initial.policy.maxTemperature));
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const editable = view.editable;
  const price = (value: number) => format.number(value, { style: "currency", currency: "USD", maximumFractionDigits: 2 });
  const number = (value: string) => (value.trim() === "" ? null : Number(value));

  function toggle(set: Set<string>, update: (s: Set<string>) => void, model: string, on: boolean) {
    const next = new Set(set);
    if (on) next.add(model);
    else next.delete(model);
    update(next);
  }

  async function save() {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch("/api/v1/ai/policy", {
        method: "PUT",
        headers: { "content-type": "application/json", "if-match": `"v${view.version}"` },
        body: JSON.stringify({
          allowlist: [...allow],
          // A model taken off the allowlist leaves every route it was on.
          routes: Object.fromEntries(Object.entries(view.policy.routes).map(([useCase, route]) => [useCase, route.filter((m) => allow.has(m))])),
          confidential: [...confidential].filter((m) => allow.has(m)),
          maxOutputTokens: number(maxTokens),
          minTemperature: number(minTemp),
          maxTemperature: number(maxTemp),
        }),
      });
      if (response.ok) {
        const saved = (await response.json()) as PolicySaveResult;
        setView(saved.policy);
        setResult({ ok: true, messages: [saved.applied ? t("saved") : t("held")] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="ai"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      {view.readOnlyReason ? (
        <p className="notice" data-testid="ai-read-only">
          {t(view.readOnlyReason === "ai.policy.read_only_package" ? "readOnly.package" : "readOnly.permission")}
        </p>
      ) : null}
      <p>{t(view.source === "platform" ? "source.platform" : "source.tenant", { version: view.version })}</p>

      <section className="ai-section" aria-labelledby="ai-catalogue-title">
        <h2 id="ai-catalogue-title">{t("catalogue.title")}</h2>
        <p>{t("catalogue.intro")}</p>
        <div className="ai-table">
          <table data-testid="ai-catalogue">
            <thead>
              <tr>
                <th scope="col">{t("catalogue.model")}</th>
                <th scope="col">{t("catalogue.provider")}</th>
                <th scope="col">{t("catalogue.regions")}</th>
                <th scope="col">{t("catalogue.retention")}</th>
                <th scope="col">{t("catalogue.ceiling")}</th>
                <th scope="col">{t("catalogue.price")}</th>
                <th scope="col">{t("catalogue.allowed")}</th>
                <th scope="col">{t("catalogue.confidential")}</th>
              </tr>
            </thead>
            <tbody>
              {catalogue.map((m) => {
                const name = modelName(m, m.model, locale);
                return (
                  <tr key={m.model}>
                    <th scope="row">
                      <bdi>{name}</bdi>
                      <br />
                      <bdi dir="ltr" className="field-hint">
                        {m.model}
                      </bdi>
                    </th>
                    <td>
                      <bdi>{m.provider}</bdi>
                    </td>
                    <td>
                      <bdi dir="ltr">{m.regions.join(", ")}</bdi>
                    </td>
                    <td>{t(m.zeroRetention && m.noTraining ? "catalogue.zeroRetention" : "catalogue.keepsData")}</td>
                    <td>{t(`classification.${m.maxClassification}`)}</td>
                    <td>{t("catalogue.priceValue", { input: price(m.inputPrice), output: price(m.outputPrice) })}</td>
                    <td>
                      <input
                        type="checkbox"
                        aria-label={t("catalogue.allowModel", { name })}
                        checked={allow.has(m.model)}
                        disabled={!editable}
                        onChange={(e) => toggle(allow, setAllow, m.model, e.target.checked)}
                      />
                    </td>
                    <td>
                      {m.confidentialEligible ? (
                        <input
                          type="checkbox"
                          aria-label={t("catalogue.confidentialModel", { name })}
                          checked={confidential.has(m.model)}
                          disabled={!editable || !allow.has(m.model)}
                          onChange={(e) => toggle(confidential, setConfidential, m.model, e.target.checked)}
                        />
                      ) : (
                        t("catalogue.notEligible")
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <section className="ai-section" aria-labelledby="ai-limits-title">
        <h2 id="ai-limits-title">{t("limits.title")}</h2>
        <FormField
          label={t("limits.maxTokens")}
          hint={t("limits.maxTokensHint")}
          name="maxOutputTokens"
          type="number"
          min={1}
          value={maxTokens}
          disabled={!editable}
          onChange={(e) => setMaxTokens(e.target.value)}
        />
        <div className="ai-row">
          <FormField
            label={t("limits.minTemperature")}
            name="minTemperature"
            type="number"
            min={0}
            max={2}
            step={0.1}
            value={minTemp}
            disabled={!editable}
            onChange={(e) => setMinTemp(e.target.value)}
          />
          <FormField
            label={t("limits.maxTemperature")}
            name="maxTemperature"
            type="number"
            min={0}
            max={2}
            step={0.1}
            value={maxTemp}
            disabled={!editable}
            onChange={(e) => setMaxTemp(e.target.value)}
          />
        </div>
      </section>

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      {editable ? (
        <div className="ai-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
            {t("save")}
          </Button>
        </div>
      ) : null}
    </form>
  );
}
