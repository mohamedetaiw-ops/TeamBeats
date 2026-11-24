"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import type { PackConnection } from "@/lib/integrations/types";

export type SmsBody = {
  displayName: string;
  rateLimitPerMinute: number;
  settings: { baseUrl: string; apiKeyRef: string; sender: string; expiresOn: string | null };
};

/**
 * Name, gateway address, API key reference and sender of an SMS gateway connection (spec 015 US6). The key stays in the
 * secret manager; only its reference is entered. The API checks the reference, the sender and the allowlist.
 */
export function SmsSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: SmsBody) => void;
}) {
  const t = useTranslations("connections.sms");
  const ts = useTranslations("connections.settings");
  const s = initial?.settings as { baseUrl?: string; apiKeyRef?: string; sender?: string; expiresOn?: string | null } | undefined;
  const [name, setName] = useState(initial?.displayName ?? "");
  const [baseUrl, setBaseUrl] = useState(s?.baseUrl ?? "");
  const [token, setToken] = useState(s?.apiKeyRef ?? "");
  const [sender, setSender] = useState(s?.sender ?? "");
  const [expiresOn, setExpiresOn] = useState(s?.expiresOn ?? "");
  const [rate, setRate] = useState(String(initial?.rateLimitPerMinute ?? 60));

  return (
    <form
      className="connections-form"
      aria-label={t("formLabel")}
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          displayName: name.trim(),
          rateLimitPerMinute: Number(rate),
          settings: { baseUrl: baseUrl.trim(), apiKeyRef: token.trim(), sender: sender.trim(), expiresOn: expiresOn || null },
        });
      }}
    >
      <FormField label={ts("name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <FormField label={t("baseUrl")} hint={t("baseUrlHint")} name="baseUrl" type="url" dir="ltr" required value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} />
      <FormField label={t("apiKey")} hint={t("apiKeyHint")} name="apiKeyRef" dir="ltr" required value={token} onChange={(e) => setToken(e.target.value)} />
      <FormField label={t("sender")} hint={t("senderHint")} name="sender" dir="ltr" required maxLength={16} value={sender} onChange={(e) => setSender(e.target.value)} />
      <FormField label={ts("expiresOn")} hint={ts("expiresOnHint")} name="expiresOn" type="date" value={expiresOn} onChange={(e) => setExpiresOn(e.target.value)} />
      <FormField label={ts("rateLimit")} hint={ts("rateLimitHint")} name="rateLimitPerMinute" type="number" min={1} max={100000} value={rate} onChange={(e) => setRate(e.target.value)} />
      <div className="connections-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={ts("working")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
