"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { AUTH_TYPES, type PackConnection, type RestAuth } from "@/lib/integrations/types";

export type SettingsBody = {
  displayName: string;
  rateLimitPerMinute: number;
  settings: { baseUrl: string; auth: RestAuth };
};

/**
 * Name, address and sign-in of a custom REST connection (spec 017 US4, FR-003). The credential is a secret manager
 * reference, never the secret itself; the API checks the reference, the allowlist and every field.
 */
export function ConnectionSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: SettingsBody) => void;
}) {
  const t = useTranslations("connections.settings");
  const authId = useId();
  const a = initial?.settings.auth;
  const [name, setName] = useState(initial?.displayName ?? "");
  const [baseUrl, setBaseUrl] = useState(initial?.settings.baseUrl ?? "https://");
  const [type, setType] = useState(a?.type ?? "bearer");
  const [secretRef, setSecretRef] = useState(a?.secretRef ?? "");
  const [header, setHeader] = useState(a?.header ?? "X-Api-Key");
  const [username, setUsername] = useState(a?.username ?? "");
  const [tokenUrl, setTokenUrl] = useState(a?.tokenUrl ?? "https://");
  const [clientId, setClientId] = useState(a?.clientId ?? "");
  const [scope, setScope] = useState(a?.scope ?? "");
  const [expiresOn, setExpiresOn] = useState(a?.expiresOn ?? "");
  const [rate, setRate] = useState(String(initial?.rateLimitPerMinute ?? 600));

  function submit() {
    const auth: RestAuth = { type, expiresOn: expiresOn || null };
    if (type !== "none") auth.secretRef = secretRef.trim();
    if (type === "api_key") auth.header = header.trim();
    if (type === "basic") auth.username = username.trim();
    if (type === "oauth_client_credentials") {
      auth.tokenUrl = tokenUrl.trim();
      auth.clientId = clientId.trim();
      auth.scope = scope.trim() || null;
    }
    onSubmit({ displayName: name.trim(), rateLimitPerMinute: Number(rate), settings: { baseUrl: baseUrl.trim(), auth } });
  }

  return (
    <form
      className="connections-form"
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <FormField label={t("name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <FormField label={t("baseUrl")} hint={t("baseUrlHint")} name="baseUrl" type="url" dir="ltr" required value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} />
      <div className="field">
        <label htmlFor={authId}>{t("authType")}</label>
        <select id={authId} value={type} onChange={(e) => setType(e.target.value)}>
          {AUTH_TYPES.map((k) => (
            <option key={k} value={k}>
              {t(`auth.${k}`)}
            </option>
          ))}
        </select>
      </div>
      {type !== "none" ? (
        <FormField label={t("secretRef")} hint={t("secretRefHint")} name="secretRef" dir="ltr" required value={secretRef} onChange={(e) => setSecretRef(e.target.value)} />
      ) : null}
      {type === "api_key" ? <FormField label={t("header")} name="header" dir="ltr" required value={header} onChange={(e) => setHeader(e.target.value)} /> : null}
      {type === "basic" ? <FormField label={t("username")} name="username" dir="ltr" required value={username} onChange={(e) => setUsername(e.target.value)} /> : null}
      {type === "oauth_client_credentials" ? (
        <>
          <FormField label={t("tokenUrl")} name="tokenUrl" type="url" dir="ltr" required value={tokenUrl} onChange={(e) => setTokenUrl(e.target.value)} />
          <FormField label={t("clientId")} name="clientId" dir="ltr" required value={clientId} onChange={(e) => setClientId(e.target.value)} />
          <FormField label={t("scope")} name="scope" dir="ltr" value={scope} onChange={(e) => setScope(e.target.value)} />
        </>
      ) : null}
      {type !== "none" ? (
        <FormField label={t("expiresOn")} hint={t("expiresOnHint")} name="expiresOn" type="date" value={expiresOn} onChange={(e) => setExpiresOn(e.target.value)} />
      ) : null}
      <FormField label={t("rateLimit")} hint={t("rateLimitHint")} name="rateLimitPerMinute" type="number" min={1} max={100000} value={rate} onChange={(e) => setRate(e.target.value)} />
      <div className="connections-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
