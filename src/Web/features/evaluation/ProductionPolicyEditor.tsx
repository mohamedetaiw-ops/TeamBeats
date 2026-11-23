"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Switch } from "@/components/ui/Switch";
import { send } from "@/lib/evaluation/send";
import type { ProductionPolicyView } from "@/lib/evaluation/types";

/**
 * The tenant's production evaluation policy (spec 034 US7, FR-023): whether live runs are sampled, at what rate and daily
 * cap, and how long sampled content is kept for review. The AI Policy Administrator changes it; the API checks the ranges.
 */
export function ProductionPolicyEditor({ initial }: { initial: ProductionPolicyView }) {
  const t = useTranslations("evaluation.production");
  const te = useTranslations("evaluation");
  const ids = useId();
  const [view, setView] = useState(initial);
  const [enabled, setEnabled] = useState(initial.enabled);
  const [rate, setRate] = useState(String(initial.ratePercent));
  const [cap, setCap] = useState(String(initial.dailyCap));
  const [retention, setRetention] = useState(String(initial.retentionDays));
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const editable = view.canEdit && view.available;

  async function save() {
    setBusy(true);
    setResult(null);
    const sent = await send<ProductionPolicyView>(
      "/api/v1/evaluation/production/policy",
      "PUT",
      { enabled, ratePercent: Number(rate), dailyCap: Number(cap), retentionDays: Number(retention) },
      te("failed"),
    );
    setBusy(false);
    if (sent.ok) {
      setView(sent.data);
      setResult({ ok: true, messages: [t("saved")] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  return (
    <div className="agents">
      <section className="agents-section" aria-labelledby={`${ids}-policy`}>
        <h2 id={`${ids}-policy`}>{t("policy")}</h2>
        <p>{t("intro")}</p>
        {!view.available ? (
          <p role="status">{t("unavailable")}</p>
        ) : (
          <div data-testid="eval-production-policy">
            <Switch label={t("enabled")} hint={t("enabledHint")} checked={enabled} disabled={!editable} onChange={(e) => setEnabled(e.target.checked)} />
            <div className="agents-row">
              <div className="field">
                <label htmlFor={`${ids}-rate`}>{t("rate")}</label>
                <input id={`${ids}-rate`} type="number" min={0.1} max={100} step="0.1" value={rate} disabled={!editable} onChange={(e) => setRate(e.target.value)} />
              </div>
              <div className="field">
                <label htmlFor={`${ids}-cap`}>{t("cap")}</label>
                <input id={`${ids}-cap`} type="number" min={1} step="1" value={cap} disabled={!editable} onChange={(e) => setCap(e.target.value)} />
              </div>
              <div className="field">
                <label htmlFor={`${ids}-retention`}>{t("retention")}</label>
                <input id={`${ids}-retention`} type="number" min={0} step="1" value={retention} disabled={!editable} onChange={(e) => setRetention(e.target.value)} />
                <p className="field-hint">{t("retentionHint")}</p>
              </div>
            </div>
            {editable ? (
              <div className="agents-row">
                <Button variant="primary" busy={busy} busyLabel={te("working")} onClick={() => void save()} data-testid="eval-production-save">
                  {t("save")}
                </Button>
              </div>
            ) : null}
          </div>
        )}
      </section>
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}
