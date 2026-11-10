"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import type { PackConnection } from "@/lib/integrations/types";

export type SlackBody = {
  displayName: string;
  rateLimitPerMinute: number;
  settings: { baseUrl: string; botTokenRef: string; expiresOn: string | null };
};

/**
 * Name, Web API address and bot token reference of a Slack connection (spec 017 US8). The token stays in the secret
 * manager; only its reference is entered. The API checks the reference and the allowlist.
 */
export function SlackSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: SlackBody) => void;
}) {
  const t = useTranslations("connections.slack");
  const ts = useTranslations("connections.settings");
  const s = initial?.settings as { baseUrl?: string; botTokenRef?: string; expiresOn?: string | null } | undefined;
  const [name, setName] = useState(initial?.displayName ?? "");
  const [baseUrl, setBaseUrl] = useState(s?.baseUrl ?? "https://slack.com/api");
  const [token, setToken] = useState(s?.botTokenRef ?? "");
  const [expiresOn, setExpiresOn] = useState(s?.expiresOn ?? "");
  const [rate, setRate] = useState(String(initial?.rateLimitPerMinute ?? 50));

  return (
    <form
      className="connections-form"
      aria-label={t("formLabel")}
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          displayName: name.trim(),
          rateLimitPerMinute: Number(rate),
          settings: { baseUrl: baseUrl.trim(), botTokenRef: token.trim(), expiresOn: expiresOn || null },
        });
      }}
    >
      <FormField label={ts("name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <FormField label={t("baseUrl")} hint={t("baseUrlHint")} name="baseUrl" type="url" dir="ltr" required value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} />
      <FormField label={t("botToken")} hint={t("botTokenHint")} name="botTokenRef" dir="ltr" required value={token} onChange={(e) => setToken(e.target.value)} />
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
