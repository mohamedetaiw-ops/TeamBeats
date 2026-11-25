"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { ERP_FINANCE_AUTH, ERP_FINANCE_SETTINGS, FINANCE_STATUSES, MAX_ERP_ACCOUNTS, type ErpFinanceKind, type ErpFinanceSettings, type PackConnection } from "@/lib/integrations/types";
import { ConnectionSettingsForm, type SettingsBody } from "./ConnectionSettingsForm";

const words = (text: string) =>
  text
    .split(",")
    .map((w) => w.trim())
    .filter(Boolean);

type Row = { category: string; account: string };

/**
 * An Oracle Fusion or SAP S/4HANA finance connection (spec 017 R3, INT-005 and INT-006): the ERP address and sign-in,
 * the ERP values a claim needs (business unit, or company code and client), the account each expense category posts
 * to, and optionally the ERP's own words for each payment status. The API checks every value and knows each ERP's
 * request shape; this form only collects the values.
 */
export function ErpFinanceSettingsForm({
  kind,
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  kind: ErpFinanceKind;
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: SettingsBody) => void;
}) {
  const t = useTranslations("connections.erpFinance");
  const s = initial?.settings as Partial<ErpFinanceSettings> | undefined;
  const fields = ERP_FINANCE_SETTINGS[kind];
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(fields.map((f) => [f.name, (s?.[f.name] as string | null | undefined) ?? ""])),
  );
  const [rows, setRows] = useState<Row[]>(() => Object.entries(s?.accounts ?? {}).map(([category, account]) => ({ category, account })));
  const [defaultAccount, setDefaultAccount] = useState(s?.defaultAccount ?? "");
  const [statusValues, setStatusValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(FINANCE_STATUSES.map((v) => [v, (s?.statusValues?.[v] ?? []).join(", ")])),
  );

  const setRow = (i: number, change: Partial<Row>) => setRows((list) => list.map((r, j) => (j === i ? { ...r, ...change } : r)));

  const extra = {
    fields: (
      <>
        <fieldset className="connections-fieldset">
          <legend>{t(`${kind}.valuesTitle`)}</legend>
          {fields.map((f) => (
            <FormField
              key={f.name}
              label={t(`${kind}.${f.name}`)}
              hint={t(`${kind}.${f.name}Hint`)}
              name={f.name}
              dir="ltr"
              required={f.required}
              value={values[f.name] ?? ""}
              onChange={(e) => setValues((m) => ({ ...m, [f.name]: e.target.value }))}
            />
          ))}
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("accountsTitle")}</legend>
          <p className="field-hint">{t(`${kind}.accountsHint`)}</p>
          <FormField label={t("defaultAccount")} name="defaultAccount" dir="ltr" required value={defaultAccount} onChange={(e) => setDefaultAccount(e.target.value)} />
          {rows.map((r, i) => (
            <div key={i} className="connections-fieldset">
              <FormField label={t("category")} hint={t("categoryHint")} name={`category-${i}`} dir="ltr" value={r.category} onChange={(e) => setRow(i, { category: e.target.value })} />
              <FormField label={t("account")} name={`account-${i}`} dir="ltr" value={r.account} onChange={(e) => setRow(i, { account: e.target.value })} />
              <Button variant="quiet" size="sm" onClick={() => setRows((list) => list.filter((_, j) => j !== i))}>
                {t("removeAccount")}
              </Button>
            </div>
          ))}
          {rows.length < MAX_ERP_ACCOUNTS ? (
            <Button variant="secondary" onClick={() => setRows((list) => [...list, { category: "", account: "" }])}>
              {t("addAccount")}
            </Button>
          ) : null}
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("statusesTitle")}</legend>
          <p className="field-hint">{t(`${kind}.statusesHint`)}</p>
          {FINANCE_STATUSES.map((v) => (
            <FormField
              key={v}
              label={t(`statuses.${v}`)}
              hint={t("wordsHint")}
              name={`status-${v}`}
              dir="ltr"
              value={statusValues[v] ?? ""}
              onChange={(e) => setStatusValues((m) => ({ ...m, [v]: e.target.value }))}
            />
          ))}
        </fieldset>
      </>
    ),
    values: (): Partial<ErpFinanceSettings> => {
      const mapped = FINANCE_STATUSES.map((v) => [v, words(statusValues[v] ?? "")] as const).filter(([, w]) => w.length > 0);
      return {
        ...Object.fromEntries(fields.map((f) => [f.name, values[f.name]?.trim() || null])),
        accounts: Object.fromEntries(rows.filter((r) => r.category.trim() && r.account.trim()).map((r) => [r.category.trim(), r.account.trim()])),
        defaultAccount: defaultAccount.trim(),
        statusValues: mapped.length > 0 ? Object.fromEntries(mapped) : null,
      };
    },
  };

  return (
    <ConnectionSettingsForm initial={initial} submitLabel={submitLabel} busy={busy} onSubmit={onSubmit} authTypes={ERP_FINANCE_AUTH} extra={extra} formLabel={t(`${kind}.formLabel`)} />
  );
}
