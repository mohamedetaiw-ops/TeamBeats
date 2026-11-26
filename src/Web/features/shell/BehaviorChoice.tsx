"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Switch } from "@/components/ui/Switch";
import { problemMessages } from "@/lib/notifications/types";
import type { BehaviorChoice as Choice } from "@/lib/shell/types";

/**
 * A person's choice about personalization from their use (spec 008 R3, US6 scenario 2). The page shows it only when the
 * API says the tenant offers it. Turning it off deletes what was counted; other settings stay.
 */
export function BehaviorChoice({ initial }: { initial: Choice }) {
  const t = useTranslations("preferences.behavior");
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string[] | null>(null);

  async function change(next: boolean) {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch("/api/v1/me/personalization/behavior", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ consented: next }),
      });
      if (response.ok) {
        setView((await response.json()) as Choice);
      } else {
        setError(await problemMessages(response));
      }
    } catch {
      setError([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div data-testid="behavior-choice">
      <Switch label={t("use")} hint={t(view.consentMode === "opt_in" ? "useHintIn" : "useHintOut")} checked={view.active} disabled={busy} onChange={(e) => void change(e.target.checked)} />
      {error ? (
        <div role="alert" className="notice notice-error">
          {error.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}
