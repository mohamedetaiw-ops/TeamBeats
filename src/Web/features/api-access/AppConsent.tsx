"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";

export type ConsentScope = { key: string; descriptionKey: string; userConsent: boolean; granted: boolean };

export type ConsentPrompt = {
  clientId: string;
  name: string;
  publisher: string | null;
  description: string | null;
  kind: string;
  scopes: ConsentScope[];
  needsAdmin: boolean;
  guestRefused: boolean;
  verified?: boolean;
};

/**
 * What an app asks to do for the person, and their decision (spec 035 FR-013). The app's name and description are shown
 * as text. Scopes that need an administrator's approval are marked, and then the person can only cancel.
 */
export function AppConsent({ prompt, query }: { prompt: ConsentPrompt; query: string }) {
  const t = useTranslations("apiAccess.consent");
  const scopes = useTranslations("apiAccess.scope");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const blocked = prompt.needsAdmin || prompt.guestRefused;

  async function allow() {
    setBusy(true);
    setFailed(false);
    const params = new URLSearchParams(query);
    const response = await fetch("/api/v1/me/app-consent", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ clientId: prompt.clientId, redirectUri: params.get("redirect_uri"), scopes: prompt.scopes.map((s) => s.key) }),
    }).catch(() => null);
    if (response?.ok) {
      // Back to the same authorization request; the API now sends the browser on to the app.
      // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- /oauth is the API's authorization endpoint, not a page
      window.location.assign(`/oauth/authorize?${query}`);
      return;
    }
    setBusy(false);
    setFailed(true);
  }

  function cancel() {
    // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- /oauth is the API's authorization endpoint, not a page
    window.location.assign(`/oauth/authorize?${query}&tb_deny=1`);
  }

  return (
    <section className="app-consent" aria-labelledby="app-consent-title">
      <h1 id="app-consent-title">{t("title", { app: prompt.name })}</h1>
      <p className="field-hint">
        {prompt.publisher ? t("publisher", { publisher: prompt.publisher }) : t("noPublisher")}{" "}
        {prompt.verified ? <Badge tone="success">{t("verified")}</Badge> : <Badge>{t("registered")}</Badge>}
      </p>
      {prompt.description ? <p>{prompt.description}</p> : null}
      <h2>{t("asks")}</h2>
      <ul className="app-consent-scopes">
        {prompt.scopes.map((scope) => (
          <li key={scope.key}>
            <span>{scopes(scope.key.replace(":", "_"))}</span>
            {!scope.granted && !scope.userConsent ? <Badge tone="warning">{t("needsAdmin")}</Badge> : null}
          </li>
        ))}
      </ul>
      {prompt.guestRefused ? (
        <Alert tone="warning" title={t("guestTitle")}>
          {t("guest")}
        </Alert>
      ) : prompt.needsAdmin ? (
        <Alert tone="warning" title={t("adminTitle")}>
          {t("admin")}
        </Alert>
      ) : (
        <p className="field-hint">{t("revokeLater")}</p>
      )}
      {failed ? (
        <Alert tone="danger" title={t("failedTitle")} live>
          {t("failed")}
        </Alert>
      ) : null}
      <div className="app-consent-actions">
        {blocked ? null : (
          <Button variant="primary" busy={busy} onClick={() => void allow()}>
            {t("allow")}
          </Button>
        )}
        <Button onClick={cancel} disabled={busy}>
          {t("cancel")}
        </Button>
      </div>
    </section>
  );
}
