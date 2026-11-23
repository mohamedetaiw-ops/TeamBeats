"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import type { PolicyView } from "@/lib/policies/types";
import { Switch } from "@/components/ui/Switch";
import "./policies.css";

/**
 * Required at onboarding (spec 025 FR-017), on a policy page for compliance officers: when on, new joiners are asked to
 * accept the policy in the attestation step of their onboarding journey. The API checks who may change it and audits it.
 */
export function OnboardingSetting({ policy }: { policy: PolicyView }) {
  const t = useTranslations("policies.onboarding");
  const ids = useId();
  const [required, setRequired] = useState(policy.onboardingRequired);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function change(next: boolean) {
    setBusy(true);
    setMessages([]);
    setSaved(false);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policy.id)}/onboarding`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ required: next }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      setRequired(((await response.json()) as PolicyView).onboardingRequired);
      setSaved(true);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="policies-card" aria-labelledby={`${ids}-title`} data-testid="onboarding-setting">
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      <Switch label={t("required")} hint={t("hint")} checked={required} disabled={busy} onChange={(e) => void change(e.target.checked)} />
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="policies-meta" role="status">
          {t(required ? "on" : "off")}
        </p>
      ) : null}
    </section>
  );
}
