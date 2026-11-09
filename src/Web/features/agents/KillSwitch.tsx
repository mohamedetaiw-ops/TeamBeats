"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { KillSwitchView } from "@/lib/agents/types";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * The tenant kill switch (spec 027 US7 scenario 2): stops every agent in the organization at its next step and
 * cancels waiting actions, while the rest of the intranet keeps working. Turning it on needs a reason and the
 * organization's name typed; the API checks both and the person's permission.
 */
export function KillSwitch({ initial }: { initial: KillSwitchView }) {
  const t = useTranslations("agents");
  const ids = useId();
  const [view, setView] = useState(initial);
  const [reason, setReason] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);

  async function change(active: boolean) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch("/api/v1/agents/kill-switch", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ active, reason, confirmation }),
      });
      if (response.ok) {
        setView((await response.json()) as KillSwitchView);
        setReason("");
        setConfirmation("");
        setResult({ ok: true, messages: [t(active ? "killSwitch.turnedOn" : "killSwitch.turnedOff")] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("killSwitch.failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="agents-section" aria-labelledby={`${ids}-title`} data-testid="agent-kill-switch">
      <h2 id={`${ids}-title`}>{t("killSwitch.title")}</h2>
      <p data-testid="agent-kill-switch-state">{view.active ? t("killSwitch.on") : t("killSwitch.off")}</p>
      <p>{t("killSwitch.intro")}</p>
      {view.active ? null : (
        <>
          <div className="field">
            <label htmlFor={`${ids}-reason`}>{t("killSwitch.reason")}</label>
            <textarea id={`${ids}-reason`} rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor={`${ids}-confirm`}>{t("killSwitch.confirmation")}</label>
            <p id={`${ids}-confirm-hint`} className="field-hint">
              {t("killSwitch.confirmationHint")}
            </p>
            <input id={`${ids}-confirm`} type="text" aria-describedby={`${ids}-confirm-hint`} value={confirmation} onChange={(e) => setConfirmation(e.target.value)} />
          </div>
        </>
      )}
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="agents-row">
        {view.active ? (
          <Button variant="primary" busy={busy} busyLabel={t("killSwitch.working")} onClick={() => void change(false)}>
            {t("killSwitch.turnOff")}
          </Button>
        ) : (
          <Button variant="destructive" busy={busy} busyLabel={t("killSwitch.working")} onClick={() => void change(true)}>
            {t("killSwitch.turnOn")}
          </Button>
        )}
      </div>
    </section>
  );
}
