"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { TextAreaField } from "@/components/ui/FormField";
import type { SensitiveFinding, SensitiveProblem } from "@/lib/security/sensitive";

const TYPES = ["saudi_id", "emirates_id", "egypt_id", "qatar_id", "iban", "payment_card", "passport"];
const PLACES = ["title", "summary", "body", "file"];

/**
 * What sensitive data detection found (spec 026 US6): each type of data and where it is, never the value. A warning
 * can be confirmed with a reason; a block can only be cleared by removing the data. The API decides which it is.
 */
export function SensitiveWarning({ problem, busy, onConfirm }: { problem: SensitiveProblem; busy: boolean; onConfirm: (reason: string) => void }) {
  const t = useTranslations("security.sensitive");
  const locale = useLocale();
  const [reason, setReason] = useState("");
  const languages = new Intl.DisplayNames([locale], { type: "language" });

  function line(f: SensitiveFinding) {
    const what = f.type === "custom" ? (f.name ?? "") : TYPES.includes(f.type) ? t(`types.${f.type}`) : f.type;
    const [place = "", language] = f.location.split(".");
    const where = PLACES.includes(place) ? t(`places.${place}`) : place;
    return t("finding", { what, where: language ? t("inLanguage", { where, language: languages.of(language) ?? language }) : where, count: f.count });
  }

  const blocked = problem.kind === "blocked";
  return (
    <Alert tone={blocked ? "danger" : "warning"} title={t(blocked ? "blockedTitle" : "foundTitle")} live>
      <p>{t(blocked ? "blockedText" : "foundText")}</p>
      <ul>
        {problem.findings.map((f) => (
          <li key={`${f.type}:${f.name ?? ""}:${f.location}`}>{line(f)}</li>
        ))}
      </ul>
      {blocked ? null : (
        <>
          <TextAreaField label={t("reason")} hint={t("reasonHint")} name="sensitiveReason" maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
          <Button variant="primary" disabled={reason.trim() === ""} busy={busy} busyLabel={t("working")} onClick={() => onConfirm(reason.trim())}>
            {t("confirm")}
          </Button>
        </>
      )}
    </Alert>
  );
}
