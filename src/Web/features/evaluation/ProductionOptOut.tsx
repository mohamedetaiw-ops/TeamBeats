"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Switch } from "@/components/ui/Switch";
import { send } from "@/lib/evaluation/send";
import type { ProductionOptOutView } from "@/lib/evaluation/types";

/**
 * A person's choice to keep their runs out of production evaluation where the tenant allows it (spec 034 US7). The page
 * shows it only when the API says production evaluation is available.
 */
export function ProductionOptOut({ initial }: { initial: ProductionOptOutView }) {
  const t = useTranslations("preferences.evaluation");
  const [optedOut, setOptedOut] = useState(initial.optedOut);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string[] | null>(null);

  async function change(next: boolean) {
    // The switch moves at once and goes back if the API refuses the change.
    const previous = optedOut;
    setOptedOut(next);
    setBusy(true);
    setError(null);
    const sent = await send<ProductionOptOutView>("/api/v1/evaluation/production/opt-out", "PUT", { optedOut: next }, t("failed"));
    setBusy(false);
    if (sent.ok) {
      setOptedOut(sent.data.optedOut);
    } else {
      setOptedOut(previous);
      setError(sent.messages);
    }
  }

  return (
    <div data-testid="eval-opt-out">
      <Switch label={t("optOut")} hint={t("optOutHint")} checked={optedOut} disabled={busy} onChange={(e) => void change(e.target.checked)} />
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
