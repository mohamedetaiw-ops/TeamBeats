"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Switch } from "@/components/ui/Switch";
import { problemMessages } from "@/lib/notifications/types";
import type { ExperimentChoice as Choice } from "@/lib/experiments";

/**
 * A person's agreement to take part in experience experiments (spec 021 R3, FR-022). Nobody takes part without it; the
 * preferences page shows it only where the package has experiments.
 */
export function ExperimentChoice({ initial }: { initial: Choice }) {
  const t = useTranslations("preferences.experiments");
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string[] | null>(null);

  async function change(next: boolean) {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch("/api/v1/me/experiments", { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ consented: next }) });
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
    <div data-testid="experiment-choice">
      <Switch label={t("use")} hint={t("hint")} checked={view.consented} disabled={busy} onChange={(e) => void change(e.target.checked)} />
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
