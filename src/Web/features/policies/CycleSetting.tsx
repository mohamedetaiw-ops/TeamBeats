"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { Button } from "@/components/ui/Button";
import { NumberField } from "@/components/ui/Inputs";
import "./policies.css";

type CycleView = { months: number | null; lastLaunchedAt: string | null; nextLaunchAt: string | null };

/**
 * Re-attestation on a fixed cycle (spec 025 FR-008), on the campaigns page: every so many months after the last
 * campaign, a new one launches for the version in force with the same settings. Empty means no cycle. The API checks
 * the range and who may set it, and audits the change.
 */
export function CycleSetting({ policyId, initial }: { policyId: string; initial: number | null }) {
  const t = useTranslations("policies.cycle");
  const ids = useId();
  const [months, setMonths] = useState<number | null>(initial);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState<CycleView | null>(null);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(null);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/cycle`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ months }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      const view = (await response.json()) as CycleView;
      setMonths(view.months);
      setSaved(view);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="policies-card" aria-labelledby={`${ids}-title`} onSubmit={(e) => void save(e)} noValidate data-testid="cycle-setting">
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      <NumberField label={t("months")} hint={t("hint")} name="cycleMonths" value={months} onValueChange={setMonths} min={1} max={60} unit={t("unit")} disabled={busy} />
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="button-row">
        <Button type="submit" busy={busy} busyLabel={t("busy")}>
          {t("save")}
        </Button>
      </div>
      {saved ? (
        <p className="policies-meta" role="status">
          {saved.months === null ? t("off") : saved.nextLaunchAt ? t("next", { date: new Date(saved.nextLaunchAt) }) : t("afterFirst")}
        </p>
      ) : null}
    </form>
  );
}
