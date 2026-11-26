"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Switch } from "@/components/ui/Switch";
import { problemMessages } from "@/lib/notifications/types";
import type { AssistantSuggestions } from "@/lib/assistant/types";

/**
 * A person's choice about the assistant's suggested questions (spec 014 R3, US9). The preferences page shows it only
 * when the API says suggestions are offered in the tenant.
 */
export function SuggestionChoice({ initial }: { initial: boolean }) {
  const t = useTranslations("preferences.assistantSuggestions");
  const [on, setOn] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string[] | null>(null);

  async function change(next: boolean) {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch("/api/v1/assistant/suggestions/preference", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ on: next }),
      });
      if (response.ok) {
        setOn(((await response.json()) as AssistantSuggestions).on);
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
    <div data-testid="assistant-suggestion-choice">
      <Switch label={t("use")} hint={t("hint")} checked={on} disabled={busy} onChange={(e) => void change(e.target.checked)} />
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
