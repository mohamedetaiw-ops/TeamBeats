"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { FormField } from "@/components/ui/FormField";
import { FINANCE_AUTH, FINANCE_FIELDS, FINANCE_LINE_FIELDS, FINANCE_STATUSES, type FinanceSettings, type PackConnection } from "@/lib/integrations/types";
import { ConnectionSettingsForm, type SettingsBody } from "./ConnectionSettingsForm";

const words = (text: string) =>
  text
    .split(",")
    .map((w) => w.trim())
    .filter(Boolean);

const filled = (map: Record<string, string>) => Object.fromEntries(Object.entries(map).filter(([, v]) => v.trim()).map(([k, v]) => [k, v.trim()]));

/**
 * A finance hand-off connection (spec 017 FR-021): the REST address and sign-in, where claims are sent and their status
 * read, where each claim field goes in the body (only mapped fields are sent), where the reference and status are in
 * the answers, and the finance system's words for each payment status. The API checks every value.
 */
export function FinanceSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: SettingsBody) => void;
}) {
  const t = useTranslations("connections.finance");
  const s = initial?.settings as Partial<FinanceSettings> | undefined;
  const [submitPath, setSubmitPath] = useState(s?.submitPath ?? "/expenses");
  const [statusPath, setStatusPath] = useState(s?.statusPath ?? "/expenses/{reference}");
  const [fields, setFields] = useState<Record<string, string>>(() => Object.fromEntries(FINANCE_FIELDS.map((f) => [f, s?.fields?.[f] ?? ""])));
  const [linesField, setLinesField] = useState(s?.linesField ?? "");
  const [lineFields, setLineFields] = useState<Record<string, string>>(() => Object.fromEntries(FINANCE_LINE_FIELDS.map((f) => [f, s?.lineFields?.[f] ?? ""])));
  const [referencePath, setReferencePath] = useState(s?.referencePath ?? "$.id");
  const [statusValuePath, setStatusValuePath] = useState(s?.statusValuePath ?? "$.status");
  const [statusValues, setStatusValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(FINANCE_STATUSES.map((v) => [v, (s?.statusValues?.[v] ?? []).join(", ")])),
  );
  const [idempotencyHeader, setIdempotencyHeader] = useState(s?.idempotencyHeader ?? "");

  const extra = {
    fields: (
      <>
        <fieldset className="connections-fieldset">
          <legend>{t("pathsTitle")}</legend>
          <FormField label={t("submitPath")} hint={t("submitPathHint")} name="submitPath" dir="ltr" required value={submitPath} onChange={(e) => setSubmitPath(e.target.value)} />
          <FormField label={t("statusPath")} hint={t("statusPathHint")} name="statusPath" dir="ltr" required value={statusPath} onChange={(e) => setStatusPath(e.target.value)} />
          <FormField label={t("idempotencyHeader")} hint={t("idempotencyHeaderHint")} name="idempotencyHeader" dir="ltr" value={idempotencyHeader} onChange={(e) => setIdempotencyHeader(e.target.value)} />
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("fieldsTitle")}</legend>
          <p className="field-hint">{t("fieldsHint")}</p>
          {FINANCE_FIELDS.map((f) => (
            <FormField key={f} label={t(`fields.${f}`)} name={`field-${f}`} dir="ltr" value={fields[f] ?? ""} onChange={(e) => setFields((m) => ({ ...m, [f]: e.target.value }))} />
          ))}
          <FormField label={t("linesField")} hint={t("linesFieldHint")} name="linesField" dir="ltr" value={linesField} onChange={(e) => setLinesField(e.target.value)} />
          {linesField.trim()
            ? FINANCE_LINE_FIELDS.map((f) => (
                <FormField key={f} label={t(`lineFields.${f}`)} name={`line-${f}`} dir="ltr" value={lineFields[f] ?? ""} onChange={(e) => setLineFields((m) => ({ ...m, [f]: e.target.value }))} />
              ))
            : null}
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("answersTitle")}</legend>
          <FormField label={t("referencePath")} hint={t("pathHint")} name="referencePath" dir="ltr" required value={referencePath} onChange={(e) => setReferencePath(e.target.value)} />
          <FormField label={t("statusValuePath")} hint={t("pathHint")} name="statusValuePath" dir="ltr" value={statusValuePath} onChange={(e) => setStatusValuePath(e.target.value)} />
          {FINANCE_STATUSES.map((v) => (
            <FormField
              key={v}
              label={t(`statuses.${v}`)}
              hint={t("wordsHint")}
              name={`status-${v}`}
              dir="ltr"
              required={v === "paid"}
              value={statusValues[v] ?? ""}
              onChange={(e) => setStatusValues((m) => ({ ...m, [v]: e.target.value }))}
            />
          ))}
        </fieldset>
      </>
    ),
    values: (): Partial<FinanceSettings> => ({
      submitPath: submitPath.trim(),
      statusPath: statusPath.trim(),
      fields: filled(fields),
      linesField: linesField.trim() || null,
      lineFields: linesField.trim() ? filled(lineFields) : null,
      referencePath: referencePath.trim(),
      statusValuePath: statusValuePath.trim() || null,
      statusValues: Object.fromEntries(FINANCE_STATUSES.map((v) => [v, words(statusValues[v] ?? "")]).filter(([, w]) => (w?.length ?? 0) > 0)),
      idempotencyHeader: idempotencyHeader.trim() || null,
    }),
  };

  return (
    <ConnectionSettingsForm initial={initial} submitLabel={submitLabel} busy={busy} onSubmit={onSubmit} authTypes={FINANCE_AUTH} extra={extra} formLabel={t("formLabel")} />
  );
}
