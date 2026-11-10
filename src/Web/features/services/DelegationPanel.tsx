"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import type { Delegation } from "@/lib/services/types";
import { isolate } from "@/lib/bidi";
import { PersonPicker } from "./PersonPicker";

/**
 * Delegation while away (spec 016 US4, "Manage Replacement"): one colleague handles your approvals from a start day
 * to an end day. Tasks reach them marked "on behalf of" you and come back when the delegation ends.
 */
export function DelegationPanel({ initial }: { initial: Delegation | null }) {
  const t = useTranslations("services.delegation");
  const format = useFormatter();
  const [current, setCurrent] = useState(initial);
  const [delegateId, setDelegateId] = useState<string | null>(initial?.delegate.id ?? null);
  const [startsOn, setStartsOn] = useState(initial?.startsOn ?? "");
  const [endsOn, setEndsOn] = useState(initial?.endsOn ?? "");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const day = (iso: string) => format.dateTime(new Date(`${iso}T12:00:00`), { dateStyle: "medium" });

  async function save() {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch("/api/v1/me/delegation", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ delegateId, startsOn: startsOn || null, endsOn: endsOn || null }),
      });
      if (response.ok) {
        setCurrent(((await response.json()) as { delegation: Delegation }).delegation);
        setResult({ ok: true, messages: [t("saved")] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  async function end() {
    setBusy(true);
    try {
      const response = await fetch("/api/v1/me/delegation", { method: "DELETE" });
      if (response.ok) {
        setCurrent(null);
        setDelegateId(null);
        setStartsOn("");
        setEndsOn("");
        setResult({ ok: true, messages: [t("ended")] });
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="services-section services-form"
      aria-labelledby="delegation-title"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <h2 id="delegation-title">{t("title")}</h2>
      <p>{t("intro")}</p>
      {current ? (
        <p className="notice" data-testid="delegation-current">
          {t(current.active ? "active" : "planned", { name: isolate(current.delegate.displayName), from: day(current.startsOn), to: day(current.endsOn) })}
        </p>
      ) : null}
      <PersonPicker label={t("delegate")} value={delegateId} onChange={setDelegateId} />
      <div className="services-row">
        <FormField label={t("startsOn")} name="startsOn" type="date" value={startsOn} onChange={(e) => setStartsOn(e.target.value)} />
        <FormField label={t("endsOn")} name="endsOn" type="date" value={endsOn} onChange={(e) => setEndsOn(e.target.value)} />
      </div>
      <div aria-live="polite">
        {result ? (
          <div className={result.ok ? "notice" : "notice notice-error"} role={result.ok ? "status" : "alert"}>
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>
      <div className="services-actions">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
          {t("save")}
        </Button>
        {current ? (
          <Button variant="destructive" onClick={() => void end()}>
            {t("end")}
          </Button>
        ) : null}
      </div>
    </form>
  );
}
