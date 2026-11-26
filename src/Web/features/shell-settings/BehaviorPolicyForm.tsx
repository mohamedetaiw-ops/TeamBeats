"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { SelectField } from "@/components/ui/FormField";
import { Switch } from "@/components/ui/Switch";
import { Widget } from "@/components/ui/Widget";
import { problemMessages } from "@/lib/notifications/types";
import type { BehaviorPolicy } from "@/lib/shell/types";

/**
 * The tenant's policy for personalization from use (spec 008 R3, FR-033): off by default; when on, opt-in counts only
 * people who agreed and opt-out counts everyone who has not declined. The page shows it where the package has the
 * feature; the API checks the right and the package again and audits every change.
 */
export function BehaviorPolicyForm({ initial }: { initial: BehaviorPolicy }) {
  const t = useTranslations("shell_settings.behavior");
  const [enabled, setEnabled] = useState(initial.enabled);
  const [mode, setMode] = useState(initial.consentMode);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setSaved(false);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/experience/behavior-policy", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ enabled, consentMode: mode }),
      });
      if (response.ok) {
        const view = (await response.json()) as BehaviorPolicy;
        setEnabled(view.enabled);
        setMode(view.consentMode);
        setSaved(true);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Widget title={t("title")} testId="behavior-policy">
      <p className="field-hint">{t("intro")}</p>
      {messages.length > 0 ? (
        <div role="alert" className="notice notice-error">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="notice notice-success" role="status">
          {t("saved")}
        </p>
      ) : null}
      <form onSubmit={(e) => void save(e)} noValidate>
        <Switch label={t("enabled")} hint={t("enabledHint")} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <SelectField
          label={t("consent")}
          name="consentMode"
          value={mode}
          onChange={(e) => setMode(e.target.value as BehaviorPolicy["consentMode"])}
          options={[
            { value: "opt_in", label: t("optIn") },
            { value: "opt_out", label: t("optOut") },
          ]}
        />
        <div className="button-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
            {t("save")}
          </Button>
        </div>
      </form>
    </Widget>
  );
}
