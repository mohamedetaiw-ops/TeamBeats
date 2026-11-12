"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { validPin, type Badge } from "@/lib/kiosk";
import { stepUpIfAsked } from "@/lib/security/types";
import "./kiosk.css";

/**
 * The person's own kiosk PIN (spec 030 FR-015), set or changed on their own device after a fresh sign-in. Setting it
 * also lifts a lock after too many wrong PINs. The badge itself is issued by a device administrator.
 */
export function KioskBadgeSettings({ initial }: { initial: Badge }) {
  const t = useTranslations("kiosk.badge");
  const [badge, setBadge] = useState(initial);
  const [pin, setPin] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setSaved(false);
    if (!validPin(pin)) return setError(t("pinFormat"));
    if (pin !== confirm) return setError(t("pinMismatch"));
    setBusy(true);
    try {
      const response = await fetch("/api/v1/me/kiosk-badge/pin", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ pin }),
      });
      if (await stepUpIfAsked(response, "/settings")) return;
      if (!response.ok) {
        const problem = (await response.json().catch(() => ({}))) as { detail?: string; errors?: Record<string, { message: string }[]> };
        setError(Object.values(problem.errors ?? {}).flat()[0]?.message ?? problem.detail ?? t("failed"));
        return;
      }
      setBadge((await response.json()) as Badge);
      setPin("");
      setConfirm("");
      setSaved(true);
    } catch {
      setError(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  const locked = badge.lockedUntil !== null && new Date(badge.lockedUntil) > new Date();
  return (
    <form className="kiosk-form" onSubmit={(e) => void submit(e)} noValidate>
      <p className="field-hint">{badge.pinSet ? t("pinSet") : t("noPin")}</p>
      {locked ? <Alert tone="warning" title={t("locked")} /> : null}
      {error ? (
        <Alert tone="danger" title={t("refused")} live>
          {error}
        </Alert>
      ) : null}
      {saved ? <Alert tone="success" title={t("saved")} live /> : null}
      <FormField
        label={t("newPin")}
        name="kiosk-pin"
        type="password"
        inputMode="numeric"
        pattern="[0-9]*"
        maxLength={8}
        autoComplete="new-password"
        dir="ltr"
        hint={t("pinHint")}
        value={pin}
        onChange={(e) => setPin(e.target.value.replace(/\D/g, ""))}
        required
      />
      <FormField
        label={t("confirmPin")}
        name="kiosk-confirm"
        type="password"
        inputMode="numeric"
        pattern="[0-9]*"
        maxLength={8}
        autoComplete="new-password"
        dir="ltr"
        value={confirm}
        onChange={(e) => setConfirm(e.target.value.replace(/\D/g, ""))}
        required
      />
      <div className="kiosk-actions">
        <Button type="submit" busy={busy} disabled={pin.length === 0}>
          {badge.pinSet ? t("change") : t("set")}
        </Button>
      </div>
    </form>
  );
}
