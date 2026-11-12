"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { ranges, stepUpIfAsked, type LockoutPreview, type SecurityPolicy } from "@/lib/security/types";

type Result = { ok: boolean; messages: string[] } | null;

const RETURN_TO = "/admin/security";

function RangeList({ label, hint, value, onChange }: { label: string; hint: string; value: string; onChange: (v: string) => void }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <p id={`${id}-hint`} className="field-hint">
        {hint}
      </p>
      <textarea id={id} aria-describedby={`${id}-hint`} dir="ltr" value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

/**
 * Tenant security policy (spec 026 US1, US2): network rules with the lock-out preview, MFA and session rules. The API
 * validates and authorizes every change, asks for the typed address when a change would refuse the caller, and asks
 * for a fresh sign-in on this sensitive action; this screen shows those answers and nothing more.
 */
export function PolicyEditor({ initial, etag }: { initial: SecurityPolicy; etag: string }) {
  const t = useTranslations("security.policy");
  const [policy, setPolicy] = useState(initial);
  const [version, setVersion] = useState(etag);
  const [allow, setAllow] = useState(initial.ipAllow.join("\n"));
  const [block, setBlock] = useState(initial.ipBlock.join("\n"));
  const [admin, setAdmin] = useState(initial.adminIpAllow.join("\n"));
  const [requireMfa, setRequireMfa] = useState(initial.requireMfa);
  const [stepUp, setStepUp] = useState(String(initial.stepUpMinutes));
  const [idle, setIdle] = useState(String(initial.idleMinutes));
  const [absolute, setAbsolute] = useState(String(initial.absoluteHours));
  const [maxSessions, setMaxSessions] = useState(initial.maxSessions === null ? "" : String(initial.maxSessions));
  const [remember, setRemember] = useState(String(initial.rememberDeviceDays));
  const [offlineItems, setOfflineItems] = useState(String(initial.offlineMaxItems));
  const [offlineMb, setOfflineMb] = useState(String(initial.offlineMaxMb));
  const [offlineDays, setOfflineDays] = useState(String(initial.offlineDays));
  const [preview, setPreview] = useState<LockoutPreview | null>(null);
  const [needsConfirmation, setNeedsConfirmation] = useState(false);
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const mfaId = useId();

  async function runPreview() {
    setResult(null);
    const query = new URLSearchParams({ ipAllow: ranges(allow).join(","), ipBlock: ranges(block).join(",") });
    try {
      const response = await fetch(`/api/v1/security/policy/preview?${query.toString()}`);
      if (response.ok) {
        setPreview((await response.json()) as LockoutPreview);
      } else {
        setPreview(null);
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    }
  }

  async function save() {
    setBusy(true);
    setResult(null);
    const body = {
      ipAllow: ranges(allow),
      ipBlock: ranges(block),
      ...(policy.scopedRulesAvailable ? { adminIpAllow: ranges(admin) } : {}),
      requireMfa,
      stepUpMinutes: Number(stepUp),
      idleMinutes: Number(idle),
      absoluteHours: Number(absolute),
      ...(maxSessions.trim() === "" ? { noSessionLimit: true } : { maxSessions: Number(maxSessions) }),
      rememberDeviceDays: Number(remember),
      offlineMaxItems: Number(offlineItems),
      offlineMaxMb: Number(offlineMb),
      offlineDays: Number(offlineDays),
      ...(needsConfirmation ? { confirmation } : {}),
    };
    try {
      const response = await fetch("/api/v1/security/policy", {
        method: "PUT",
        headers: { "content-type": "application/json", "if-match": version },
        body: JSON.stringify(body),
      });
      if (await stepUpIfAsked(response, RETURN_TO)) return;
      if (response.ok) {
        const saved = (await response.json()) as SecurityPolicy;
        setPolicy(saved);
        setVersion(response.headers.get("etag") ?? version);
        setNeedsConfirmation(false);
        setConfirmation("");
        setResult({ ok: true, messages: [t("saved")] });
      } else {
        const problem = (await response.clone().json().catch(() => null)) as { errors?: Record<string, unknown> } | null;
        if (problem?.errors && "confirmation" in problem.errors) setNeedsConfirmation(true);
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const mfaLocked = !policy.mfaClaimSeen && !policy.requireMfa;

  return (
    <form
      className="security"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <section className="security-section" aria-labelledby="security-network-title">
        <h2 id="security-network-title">{t("network.title")}</h2>
        <p>{t("network.intro")}</p>
        {policy.yourAddress ? (
          <p>
            {t("network.yourAddress")} <bdi dir="ltr">{policy.yourAddress}</bdi>
          </p>
        ) : null}
        <RangeList label={t("network.allow")} hint={t("network.allowHint")} value={allow} onChange={setAllow} />
        <RangeList label={t("network.block")} hint={t("network.blockHint")} value={block} onChange={setBlock} />
        {policy.scopedRulesAvailable ? (
          <RangeList label={t("network.admin")} hint={t("network.adminHint")} value={admin} onChange={setAdmin} />
        ) : (
          <p className="notice">{t("network.adminUnavailable")}</p>
        )}
        <div className="security-row">
          <Button onClick={() => void runPreview()}>{t("network.preview")}</Button>
        </div>
        {preview ? (
          <div role="status" className={preview.youWouldBeRefused ? "notice notice-error" : "notice"}>
            <p>{t("network.previewResult", { count: preview.peopleOutside })}</p>
            {preview.youWouldBeRefused ? <p>{t("network.previewYou")}</p> : null}
          </div>
        ) : null}
        {needsConfirmation ? (
          <FormField
            label={t("network.confirm")}
            hint={t("network.confirmHint")}
            name="confirmation"
            dir="ltr"
            autoComplete="off"
            value={confirmation}
            onChange={(e) => setConfirmation(e.target.value)}
          />
        ) : null}
      </section>

      <section className="security-section" aria-labelledby="security-mfa-title">
        <h2 id="security-mfa-title">{t("mfa.title")}</h2>
        <div className="field">
          <label htmlFor={mfaId}>
            <input
              id={mfaId}
              type="checkbox"
              checked={requireMfa}
              disabled={mfaLocked}
              aria-describedby={`${mfaId}-hint`}
              onChange={(e) => setRequireMfa(e.target.checked)}
            />{" "}
            {t("mfa.require")}
          </label>
          <p id={`${mfaId}-hint`} className="field-hint">
            {mfaLocked ? t("mfa.notSeen") : t("mfa.hint")}
          </p>
        </div>
        <FormField label={t("mfa.stepUp")} hint={t("mfa.stepUpHint")} name="stepUpMinutes" type="number" min={1} max={60} value={stepUp} onChange={(e) => setStepUp(e.target.value)} />
      </section>

      <section className="security-section" aria-labelledby="security-sessions-title">
        <h2 id="security-sessions-title">{t("sessions.title")}</h2>
        <FormField label={t("sessions.idle")} hint={t("sessions.idleHint")} name="idleMinutes" type="number" min={5} max={1440} value={idle} onChange={(e) => setIdle(e.target.value)} />
        <FormField label={t("sessions.absolute")} hint={t("sessions.absoluteHint")} name="absoluteHours" type="number" min={1} max={72} value={absolute} onChange={(e) => setAbsolute(e.target.value)} />
        <FormField label={t("sessions.max")} hint={t("sessions.maxHint")} name="maxSessions" type="number" min={1} max={20} value={maxSessions} onChange={(e) => setMaxSessions(e.target.value)} />
        <FormField label={t("sessions.remember")} hint={t("sessions.rememberHint")} name="rememberDeviceDays" type="number" min={0} max={90} value={remember} onChange={(e) => setRemember(e.target.value)} />
      </section>

      <section className="security-section" aria-labelledby="security-offline-title">
        <h2 id="security-offline-title">{t("offline.title")}</h2>
        <p className="field-hint">{t("offline.hint")}</p>
        <FormField label={t("offline.items")} hint={t("offline.itemsHint")} name="offlineMaxItems" type="number" min={0} max={200} value={offlineItems} onChange={(e) => setOfflineItems(e.target.value)} />
        <FormField label={t("offline.mb")} name="offlineMaxMb" type="number" min={1} max={200} value={offlineMb} onChange={(e) => setOfflineMb(e.target.value)} />
        <FormField label={t("offline.days")} name="offlineDays" type="number" min={1} max={30} value={offlineDays} onChange={(e) => setOfflineDays(e.target.value)} />
      </section>

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      <div className="security-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
          {t("save")}
        </Button>
      </div>
    </form>
  );
}
