"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { kioskCall, type KioskDeviceInfo } from "@/lib/kiosk";

/** Turns this tablet into a kiosk with the one-time code a device administrator created (spec 030 FR-014). */
export function KioskEnrol() {
  const t = useTranslations("kiosk.enrol");
  const router = useRouter();
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    const result = await kioskCall<KioskDeviceInfo>("/api/v1/kiosk/enrol", "POST", { code });
    setBusy(false);
    if (result.ok) {
      router.refresh();
      return;
    }
    setError(result.refusal.messages[0] ?? t("failed"));
  }

  return (
    <section className="kiosk-panel" aria-labelledby="kiosk-enrol-title">
      <h2 id="kiosk-enrol-title">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      <form onSubmit={(e) => void submit(e)} noValidate>
        <FormField
          label={t("code")}
          name="code"
          hint={t("codeHint")}
          value={code}
          onChange={(e) => setCode(e.target.value)}
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          dir="ltr"
          required
          error={error}
        />
        <Button type="submit" busy={busy} disabled={code.trim().length === 0}>
          {t("submit")}
        </Button>
      </form>
      {error ? null : <Alert title={t("whoTitle")}>{t("who")}</Alert>}
    </section>
  );
}
