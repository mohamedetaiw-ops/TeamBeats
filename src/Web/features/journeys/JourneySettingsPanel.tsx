"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { problemMessages } from "@/lib/notifications/types";
import type { JourneySettings } from "@/lib/journeys";
import "./journeys.css";

/**
 * Pre-boarding (spec 022 FR-016): the tenant setting that sends hires without an account a time-limited link to their
 * personal email from HR, and how those invitations went. The API decides who may change it (an HR officer of the
 * whole organization) and keeps it versioned; this panel only shows and sends the choice.
 */
export function JourneySettingsPanel({ initial }: { initial: JourneySettings }) {
  const t = useTranslations("journeys.settings");
  const [settings, setSettings] = useState(initial);
  const [enabled, setEnabled] = useState(initial.preboardingEnabled);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function save() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/hr/journey-settings", {
        method: "PUT",
        headers: { "content-type": "application/json", "If-Match": settings.eTag },
        body: JSON.stringify({ preboardingEnabled: enabled }),
      });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      const next = (await response.json()) as JourneySettings;
      setSettings(next);
      setEnabled(next.preboardingEnabled);
      setNotice(t("saved"));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const counts = settings.preboarding;
  return (
    <section className="journeys-section" aria-labelledby="journey-settings" id="journey-preboarding">
      <h2 id="journey-settings">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <Checkbox
        label={t("enabled")}
        hint={settings.canChange ? t("enabledHint") : t("readOnly")}
        name="preboarding-enabled"
        checked={enabled}
        disabled={!settings.canChange || busy}
        onChange={(e) => setEnabled(e.target.checked)}
      />
      {settings.canChange ? (
        <Button variant="primary" busy={busy} busyLabel={t("saving")} disabled={enabled === settings.preboardingEnabled} onClick={() => void save()}>
          {t("save")}
        </Button>
      ) : null}
      <dl className="journeys-details">
        <div>
          <dt>{t("scheduled")}</dt>
          <dd>{counts.scheduled}</dd>
        </div>
        <div>
          <dt>{t("sent")}</dt>
          <dd>{counts.sent}</dd>
        </div>
        <div>
          <dt>{t("opened")}</dt>
          <dd>{counts.opened}</dd>
        </div>
        <div>
          <dt>{t("failedCount")}</dt>
          <dd>{counts.failed}</dd>
        </div>
      </dl>
    </section>
  );
}
