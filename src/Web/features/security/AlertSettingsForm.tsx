"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { SelectField } from "@/components/ui/FormField";
import { NumberField, TimeField } from "@/components/ui/Inputs";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import type { AlertSettings } from "@/lib/security/types";

const RETURN_TO = "/admin/security/alerts";
const MB = 1_000_000;

/**
 * Alert thresholds (spec 026 FR-013, US5): download volume, working hours, refused app calls, app call volume, and who
 * hears about guests opening Confidential items. Everyone who sees alerts sees them; the API decides who may change
 * them on this plan and asks for a fresh sign-in.
 */
export function AlertSettingsForm({ initial }: { initial: AlertSettings }) {
  const t = useTranslations("security.alerts.settings");
  const action = useAdminAction(RETURN_TO);
  const [files, setFiles] = useState<number | null>(initial.downloadFiles);
  const [mb, setMb] = useState<number | null>(Math.round(initial.downloadBytes / MB));
  const [workStart, setWorkStart] = useState(initial.workStart);
  const [workEnd, setWorkEnd] = useState(initial.workEnd);
  const [denied, setDenied] = useState<number | null>(initial.apiDeniedCount);
  const [factor, setFactor] = useState<number | null>(initial.apiVolumeFactor);
  const [notify, setNotify] = useState<string>(initial.guestConfidentialNotify);
  const locked = !initial.editable;

  async function save(e: FormEvent) {
    e.preventDefault();
    await action.run("save", "PUT", "/api/v1/security/alert-settings", {
      downloadFiles: files,
      downloadBytes: mb === null ? null : mb * MB,
      workStart,
      workEnd,
      apiDeniedCount: denied,
      apiVolumeFactor: factor,
      guestConfidentialNotify: notify,
    }, t("saved"));
  }

  return (
    <section className="security" aria-labelledby="alert-settings-title">
      <h2 id="alert-settings-title">{t("title")}</h2>
      <p className="notice">{locked ? t("locked") : t("intro")}</p>
      <ResultAlert result={action.result} />
      <form onSubmit={(e) => void save(e)}>
        <div className="admin-grid">
          <NumberField label={t("downloadFiles")} hint={t("downloadFilesHint")} name="downloadFiles" value={files} onValueChange={setFiles} min={10} max={100000} disabled={locked} />
          <NumberField label={t("downloadMb")} hint={t("downloadMbHint")} name="downloadMb" value={mb} onValueChange={setMb} min={10} max={1000000} step={100} unit={t("mb")} disabled={locked} />
          <TimeField label={t("workStart")} name="workStart" value={workStart} onChange={(e) => setWorkStart(e.target.value)} disabled={locked} />
          <TimeField label={t("workEnd")} hint={t("workHint")} name="workEnd" value={workEnd} onChange={(e) => setWorkEnd(e.target.value)} disabled={locked} />
          <NumberField label={t("apiDenied")} hint={t("apiDeniedHint")} name="apiDeniedCount" value={denied} onValueChange={setDenied} min={5} max={10000} disabled={locked} />
          <NumberField label={t("apiFactor")} hint={t("apiFactorHint")} name="apiVolumeFactor" value={factor} onValueChange={setFactor} min={2} max={100} step={0.5} disabled={locked} />
          <SelectField
            label={t("guestNotify")}
            hint={t("guestNotifyHint")}
            name="guestConfidentialNotify"
            value={notify}
            onChange={(e) => setNotify(e.target.value)}
            disabled={locked}
            options={(["security", "sponsor", "both"] as const).map((v) => ({ value: v, label: t(`notify.${v}`) }))}
          />
        </div>
        {locked ? null : (
          <div className="ui-control-row">
            <Button type="submit" variant="primary" busy={action.busy === "save"} busyLabel={t("working")}>
              {t("save")}
            </Button>
          </div>
        )}
      </form>
    </section>
  );
}
