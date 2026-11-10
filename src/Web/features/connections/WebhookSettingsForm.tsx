"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import {
  WEBHOOK_EVENTS,
  WEBHOOK_TARGETS,
  formatPairs,
  known,
  parsePairs,
  type PackConnection,
  type WebhookEventChoice,
  type WebhookSettings,
} from "@/lib/integrations/types";

export type WebhookBody = { displayName: string; rateLimitPerMinute: number; settings: WebhookSettings };

/**
 * Settings of a webhook connection (spec 017 US5): where events go and which fields of each, the signing secret as a
 * secret manager reference with an optional rotation overlap, and how inbound deliveries are mapped. The event list
 * and its fields come from the API, which checks every value again.
 */
export function WebhookSettingsForm({
  initial,
  events,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  events: WebhookEventChoice[];
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: WebhookBody) => void;
}) {
  const t = useTranslations("connections");
  const targetId = useId();
  const s = initial?.settings as unknown as WebhookSettings | undefined;
  const [name, setName] = useState(initial?.displayName ?? "");
  const [url, setUrl] = useState(s?.url ?? "");
  const [secretRef, setSecretRef] = useState(s?.auth?.secretRef ?? "");
  const [overlap, setOverlap] = useState("0");
  const [chosen, setChosen] = useState<Record<string, string[]>>(Object.fromEntries((s?.events ?? []).map((e) => [e.type, e.fields])));
  const [inboundOn, setInboundOn] = useState(Boolean(s?.inbound));
  const [target, setTarget] = useState(s?.inbound?.target ?? "event");
  const [targetName, setTargetName] = useState(s?.inbound?.name ?? "");
  const [correlation, setCorrelation] = useState(s?.inbound?.correlationPath ?? "");
  const [fields, setFields] = useState(formatPairs(s?.inbound?.fields));
  const [windowSeconds, setWindowSeconds] = useState(String(s?.inbound?.windowSeconds ?? 300));
  const [rate, setRate] = useState(String(initial?.rateLimitPerMinute ?? 600));
  const secretChanged = initial !== undefined && secretRef.trim() !== (s?.auth?.secretRef ?? "");

  function toggle(type: string, field: string | null, on: boolean) {
    setChosen((c) => {
      const next = { ...c };
      const all = events.find((e) => e.type === type)?.fields ?? [];
      if (field === null) {
        if (on) next[type] = all;
        else delete next[type];
      } else {
        const list = new Set(next[type] ?? []);
        if (on) list.add(field);
        else list.delete(field);
        if (list.size === 0) delete next[type];
        else next[type] = all.filter((f) => list.has(f));
      }
      return next;
    });
  }

  function submit() {
    const settings: WebhookSettings = {
      url: url.trim() || null,
      auth: { type: "hmac_sha256", secretRef: secretRef.trim(), overlapHours: secretChanged ? Number(overlap) : null },
      events: Object.entries(chosen).map(([type, f]) => ({ type, fields: f })),
      inbound: inboundOn
        ? { target, name: targetName.trim(), correlationPath: correlation.trim() || null, fields: parsePairs(fields), windowSeconds: Number(windowSeconds) }
        : null,
    };
    onSubmit({ displayName: name.trim(), rateLimitPerMinute: Number(rate), settings });
  }

  const eventName = (type: string) => (known(WEBHOOK_EVENTS, type) ? t(`webhooks.events.${type}`) : type);

  return (
    <form
      className="connections-form"
      aria-label={t("webhooks.formLabel")}
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <FormField label={t("webhooks.name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <FormField label={t("webhooks.url")} hint={t("webhooks.urlHint")} name="url" type="url" dir="ltr" value={url} onChange={(e) => setUrl(e.target.value)} />
      <FormField label={t("webhooks.secretRef")} hint={t("settings.secretRefHint")} name="secretRef" dir="ltr" required value={secretRef} onChange={(e) => setSecretRef(e.target.value)} />
      {secretChanged ? (
        <FormField label={t("webhooks.overlap")} hint={t("webhooks.overlapHint")} name="overlapHours" type="number" min={0} max={24} value={overlap} onChange={(e) => setOverlap(e.target.value)} />
      ) : null}
      {s?.auth?.previousValidUntil ? <p className="notice">{t("webhooks.overlapRunning")}</p> : null}

      <fieldset className="connections-fieldset">
        <legend>{t("webhooks.eventsTitle")}</legend>
        <p className="field-hint">{t("webhooks.eventsHint")}</p>
        {events.map((e) => (
          <div key={e.type} className="connections-form">
            <label className="connections-check">
              <input type="checkbox" checked={e.type in chosen} onChange={(ev) => toggle(e.type, null, ev.target.checked)} />
              {eventName(e.type)}
            </label>
            {e.type in chosen ? (
              <div className="connections-row">
                {e.fields.map((f) => (
                  <label key={f} className="connections-check">
                    <input
                      type="checkbox"
                      aria-label={t("webhooks.fieldOf", { field: f, event: eventName(e.type) })}
                      checked={chosen[e.type]?.includes(f) ?? false}
                      onChange={(ev) => toggle(e.type, f, ev.target.checked)}
                    />
                    <bdi dir="ltr">{f}</bdi>
                  </label>
                ))}
              </div>
            ) : null}
          </div>
        ))}
      </fieldset>

      <fieldset className="connections-fieldset">
        <legend>{t("webhooks.inboundTitle")}</legend>
        <label className="connections-check">
          <input type="checkbox" checked={inboundOn} onChange={(e) => setInboundOn(e.target.checked)} />
          {t("webhooks.inboundOn")}
        </label>
        {inboundOn ? (
          <>
            <div className="field">
              <label htmlFor={targetId}>{t("webhooks.target")}</label>
              <select id={targetId} value={target} onChange={(e) => setTarget(e.target.value)}>
                {WEBHOOK_TARGETS.map((k) => (
                  <option key={k} value={k}>
                    {t(`webhooks.targets.${k}`)}
                  </option>
                ))}
              </select>
            </div>
            <FormField label={t("webhooks.targetName")} hint={t("webhooks.targetNameHint")} name="targetName" dir="ltr" required value={targetName} onChange={(e) => setTargetName(e.target.value)} />
            <FormField
              label={t("webhooks.correlationPath")}
              hint={t("webhooks.correlationPathHint")}
              name="correlationPath"
              dir="ltr"
              required={target === "message"}
              value={correlation}
              onChange={(e) => setCorrelation(e.target.value)}
            />
            <div className="field">
              <label htmlFor={`${targetId}-fields`}>{t("webhooks.fields")}</label>
              <p className="field-hint">{t("webhooks.fieldsHint")}</p>
              <textarea id={`${targetId}-fields`} dir="ltr" value={fields} onChange={(e) => setFields(e.target.value)} />
            </div>
            <FormField label={t("webhooks.window")} hint={t("webhooks.windowHint")} name="windowSeconds" type="number" min={60} max={300} value={windowSeconds} onChange={(e) => setWindowSeconds(e.target.value)} />
          </>
        ) : null}
      </fieldset>

      <FormField label={t("settings.rateLimit")} hint={t("settings.rateLimitHint")} name="rateLimitPerMinute" type="number" min={1} max={100000} value={rate} onChange={(e) => setRate(e.target.value)} />
      <div className="connections-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("settings.working")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
