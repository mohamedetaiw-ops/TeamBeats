"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { notificationChannels, type EffortEstimatesView } from "@/lib/analytics/types";
import { problemMessages } from "@/lib/notifications/types";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { Alert } from "@/components/ui/Alert";

/** The tenant's effort estimates (spec 021 US2 scenario 2), saved as a new version each time. */
export function EstimatesForm({ view }: { view: EffortEstimatesView }) {
  const t = useTranslations("analytics.estimates");
  const router = useRouter();
  const e = view.estimates;
  const [hours, setHours] = useState<number | null>(e?.authoringHoursPerItem ?? null);
  const [rate, setRate] = useState<number | null>(e?.costPerHour ?? null);
  const [currency, setCurrency] = useState(e?.currency ?? "");
  const [sends, setSends] = useState<Record<string, number | null>>(() =>
    Object.fromEntries(notificationChannels.map((c) => [c, e?.costPerSend[c] ?? null])),
  );
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setResult(null);
    try {
      const costPerSend = Object.fromEntries(Object.entries(sends).filter((entry): entry is [string, number] => entry[1] !== null));
      const response = await fetch("/api/v1/analytics/estimates", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ authoringHoursPerItem: hours, costPerHour: rate, currency: currency.trim().toUpperCase(), costPerSend, version: view.version }),
      });
      if (!response.ok) {
        setResult({ ok: false, messages: await problemMessages(response) });
        return;
      }
      setResult({ ok: true, messages: [t("saved")] });
      router.refresh();
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="analytics-card" onSubmit={save} data-testid="estimates-form">
      {result ? <Alert tone={result.ok ? "success" : "danger"} title={result.messages.join(" ")} /> : null}
      <NumberField name="hours" label={t("hours")} hint={t("hoursHint")} value={hours} onValueChange={setHours} min={0} max={1000} step={0.5} disabled={!view.canChange} />
      <NumberField name="rate" label={t("rate")} value={rate} onValueChange={setRate} min={0} max={1000000} step={1} disabled={!view.canChange} />
      <FormField
        label={t("currency")}
        hint={t("currencyHint")}
        name="currency"
        value={currency}
        onChange={(ev) => setCurrency(ev.target.value)}
        maxLength={3}
        dir="ltr"
        disabled={!view.canChange}
      />
      <fieldset className="analytics-card">
        <legend>{t("perSend")}</legend>
        {notificationChannels.map((c) => (
          <NumberField
            key={c}
            name={`send-${c}`}
            label={t(`channel.${c}`)}
            value={sends[c] ?? null}
            onValueChange={(v) => setSends((s) => ({ ...s, [c]: v }))}
            min={0}
            max={1000}
            step={0.01}
            disabled={!view.canChange}
          />
        ))}
      </fieldset>
      {view.canChange ? (
        <div>
          <Button type="submit" variant="primary" disabled={busy}>
            {t("save")}
          </Button>
        </div>
      ) : (
        <p className="analytics-meta">{t("readOnly")}</p>
      )}
    </form>
  );
}
