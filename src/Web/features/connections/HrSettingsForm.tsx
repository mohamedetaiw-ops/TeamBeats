"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import {
  CLOCK_AUTH,
  CLOCK_DELIMITERS,
  HR_AUTH,
  HR_FIELDS,
  HR_PAGING,
  HR_PAYSLIP_FIELDS,
  HR_REQUIRED,
  HR_STATUSES,
  type HrMappingSettings,
  type HrRestSettings,
  type HrSftpSettings,
  type PackConnection,
} from "@/lib/integrations/types";
import { ConnectionSettingsForm, type SettingsBody } from "./ConnectionSettingsForm";

export type HrSftpBody = { displayName: string; settings: Partial<HrSftpSettings> };

const words = (text: string) =>
  text
    .split(",")
    .map((w) => w.trim())
    .filter(Boolean);

const blank = (v: string) => (v.trim() ? v.trim() : null);

/** The mapping both HR packs share (spec 019 FR-016): where each canonical field is, the status words, date format and nightly time. */
function useMapping(s: Partial<HrMappingSettings> | undefined) {
  const [fields, setFields] = useState<Record<string, string>>(() => Object.fromEntries(HR_FIELDS.map((f) => [f, s?.fields?.[f] ?? ""])));
  const [statuses, setStatuses] = useState<Record<string, string>>(() => Object.fromEntries(HR_STATUSES.map((v) => [v, (s?.statusValues?.[v] ?? []).join(", ")])));
  const [dateFormat, setDateFormat] = useState(s?.dateFormat ?? "");
  const [syncAt, setSyncAt] = useState(s?.syncAt ?? "01:00");
  const [timeZone, setTimeZone] = useState(s?.timeZone ?? "UTC");
  return {
    render: (t: ReturnType<typeof useTranslations>, hint: string) => (
      <>
        <fieldset className="connections-fieldset">
          <legend>{t("fieldsTitle")}</legend>
          <p className="field-hint">{hint}</p>
          {HR_FIELDS.map((f) => (
            <FormField
              key={f}
              label={t(`fields.${f}`)}
              name={`field-${f}`}
              dir="ltr"
              required={(HR_REQUIRED as readonly string[]).includes(f)}
              value={fields[f] ?? ""}
              onChange={(e) => setFields((m) => ({ ...m, [f]: e.target.value }))}
            />
          ))}
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("statusesTitle")}</legend>
          {HR_STATUSES.map((v) => (
            <FormField
              key={v}
              label={t(`statuses.${v}`)}
              hint={t("wordsHint")}
              name={`status-${v}`}
              dir="ltr"
              required
              value={statuses[v] ?? ""}
              onChange={(e) => setStatuses((m) => ({ ...m, [v]: e.target.value }))}
            />
          ))}
          <FormField label={t("dateFormat")} hint={t("dateFormatHint")} name="dateFormat" dir="ltr" value={dateFormat} onChange={(e) => setDateFormat(e.target.value)} />
          <FormField label={t("syncAt")} hint={t("syncAtHint")} name="syncAt" type="time" value={syncAt} onChange={(e) => setSyncAt(e.target.value)} />
          <FormField label={t("timeZone")} hint={t("timeZoneHint")} name="timeZone" dir="ltr" required value={timeZone} onChange={(e) => setTimeZone(e.target.value)} />
        </fieldset>
      </>
    ),
    values: (): HrMappingSettings => ({
      fields: Object.fromEntries(Object.entries(fields).filter(([, v]) => v.trim()).map(([k, v]) => [k, v.trim()])),
      statusValues: Object.fromEntries(HR_STATUSES.map((v) => [v, words(statuses[v] ?? "")]).filter(([, w]) => (w?.length ?? 0) > 0)),
      dateFormat: blank(dateFormat),
      syncAt: syncAt || null,
      timeZone: timeZone.trim(),
    }),
  };
}

/**
 * The generic HR REST connection (spec 019 US1): the HR system's address and sign-in, where the employee list is and
 * how it pages, and where each canonical field is in a record. Only one HR connection is allowed; the API checks every value.
 */
export function HrRestSettingsForm({
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
  const t = useTranslations("connections.hr");
  const pagingId = useId();
  const s = initial?.settings as Partial<HrRestSettings> | undefined;
  const mapping = useMapping(s);
  const [employeesPath, setEmployeesPath] = useState(s?.employeesPath ?? "/employees");
  const [recordsPath, setRecordsPath] = useState(s?.recordsPath ?? "$.items");
  const [paging, setPaging] = useState<string>(s?.paging ?? "none");
  const [pageParam, setPageParam] = useState(s?.pageParam ?? "page");
  const [pageSizeParam, setPageSizeParam] = useState(s?.pageSizeParam ?? "");
  const [pageSize, setPageSize] = useState(String(s?.pageSize ?? 200));
  const [nextPath, setNextPath] = useState(s?.nextPath ?? "$.next");
  const [employeePath, setEmployeePath] = useState(s?.employeePath ?? "");
  const [employeeRecordPath, setEmployeeRecordPath] = useState(s?.employeeRecordPath ?? "");
  const [payslipsPath, setPayslipsPath] = useState(s?.payslipsPath ?? "");
  const [payslipRecordsPath, setPayslipRecordsPath] = useState(s?.payslipRecordsPath ?? "");
  const [payslipPdfPath, setPayslipPdfPath] = useState(s?.payslipPdfPath ?? "");
  const [payslipFields, setPayslipFields] = useState<Record<string, string>>(() => ({ ...(s?.payslipFields ?? {}) }));
  const [leaveBalancesPath, setLeaveBalancesPath] = useState(s?.leaveBalancesPath ?? "");
  const [leaveBalanceRecordsPath, setLeaveBalanceRecordsPath] = useState(s?.leaveBalanceRecordsPath ?? "");
  const [leaveType, setLeaveType] = useState(s?.leaveBalanceFields?.type ?? "");
  const [leaveBalance, setLeaveBalance] = useState(s?.leaveBalanceFields?.balance ?? "");
  const [leaveSubmitPath, setLeaveSubmitPath] = useState(s?.leaveSubmitPath ?? "");
  const [leaveReferencePath, setLeaveReferencePath] = useState(s?.leaveReferencePath ?? "");
  const [leaveIdempotencyHeader, setLeaveIdempotencyHeader] = useState(s?.leaveIdempotencyHeader ?? "");
  const [leaveTypeCodes, setLeaveTypeCodes] = useState(() =>
    Object.entries(s?.leaveTypeCodes ?? {})
      .map(([k, v]) => `${k}=${v}`)
      .join("\n"),
  );

  const extra = {
    fields: (
      <>
        <fieldset className="connections-fieldset">
          <legend>{t("listTitle")}</legend>
          <FormField label={t("employeesPath")} hint={t("employeesPathHint")} name="employeesPath" dir="ltr" required value={employeesPath} onChange={(e) => setEmployeesPath(e.target.value)} />
          <FormField label={t("recordsPath")} hint={t("pathHint")} name="recordsPath" dir="ltr" value={recordsPath} onChange={(e) => setRecordsPath(e.target.value)} />
          <div className="field">
            <label htmlFor={pagingId}>{t("paging")}</label>
            <select id={pagingId} value={paging} onChange={(e) => setPaging(e.target.value)}>
              {HR_PAGING.map((k) => (
                <option key={k} value={k}>
                  {t(`pagings.${k}`)}
                </option>
              ))}
            </select>
          </div>
          {paging === "page" ? (
            <>
              <FormField label={t("pageParam")} name="pageParam" dir="ltr" required value={pageParam} onChange={(e) => setPageParam(e.target.value)} />
              <FormField label={t("pageSizeParam")} name="pageSizeParam" dir="ltr" value={pageSizeParam} onChange={(e) => setPageSizeParam(e.target.value)} />
              <FormField label={t("pageSize")} name="pageSize" type="number" min={1} max={1000} value={pageSize} onChange={(e) => setPageSize(e.target.value)} />
            </>
          ) : null}
          {paging === "next" ? <FormField label={t("nextPath")} hint={t("nextPathHint")} name="nextPath" dir="ltr" required value={nextPath} onChange={(e) => setNextPath(e.target.value)} /> : null}
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("oneTitle")}</legend>
          <FormField label={t("employeePath")} hint={t("employeePathHint")} name="employeePath" dir="ltr" value={employeePath} onChange={(e) => setEmployeePath(e.target.value)} />
          <FormField label={t("employeeRecordPath")} hint={t("pathHint")} name="employeeRecordPath" dir="ltr" value={employeeRecordPath} onChange={(e) => setEmployeeRecordPath(e.target.value)} />
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("payslipsTitle")}</legend>
          <FormField label={t("payslipsPath")} hint={t("payslipsPathHint")} name="payslipsPath" dir="ltr" value={payslipsPath} onChange={(e) => setPayslipsPath(e.target.value)} />
          {payslipsPath.trim() ? (
            <>
              <FormField label={t("payslipRecordsPath")} hint={t("pathHint")} name="payslipRecordsPath" dir="ltr" value={payslipRecordsPath} onChange={(e) => setPayslipRecordsPath(e.target.value)} />
              {HR_PAYSLIP_FIELDS.map((f) => (
                <FormField
                  key={f}
                  label={t(`payslipFields.${f}`)}
                  name={`payslip-${f}`}
                  dir="ltr"
                  required={f === "period" || f === "net"}
                  value={payslipFields[f] ?? ""}
                  onChange={(e) => setPayslipFields((m) => ({ ...m, [f]: e.target.value }))}
                />
              ))}
              <FormField label={t("payslipPdfPath")} hint={t("payslipPdfPathHint")} name="payslipPdfPath" dir="ltr" value={payslipPdfPath} onChange={(e) => setPayslipPdfPath(e.target.value)} />
            </>
          ) : null}
        </fieldset>
        <fieldset className="connections-fieldset">
          <legend>{t("leaveTitle")}</legend>
          <FormField label={t("leaveBalancesPath")} hint={t("leaveBalancesPathHint")} name="leaveBalancesPath" dir="ltr" value={leaveBalancesPath} onChange={(e) => setLeaveBalancesPath(e.target.value)} />
          {leaveBalancesPath.trim() ? (
            <>
              <FormField label={t("leaveBalanceRecordsPath")} hint={t("pathHint")} name="leaveBalanceRecordsPath" dir="ltr" value={leaveBalanceRecordsPath} onChange={(e) => setLeaveBalanceRecordsPath(e.target.value)} />
              <FormField label={t("leaveBalanceType")} name="leaveBalanceType" dir="ltr" required value={leaveType} onChange={(e) => setLeaveType(e.target.value)} />
              <FormField label={t("leaveBalanceValue")} name="leaveBalanceValue" dir="ltr" required value={leaveBalance} onChange={(e) => setLeaveBalance(e.target.value)} />
            </>
          ) : null}
          <FormField label={t("leaveSubmitPath")} hint={t("leaveSubmitPathHint")} name="leaveSubmitPath" dir="ltr" value={leaveSubmitPath} onChange={(e) => setLeaveSubmitPath(e.target.value)} />
          {leaveSubmitPath.trim() ? (
            <>
              <FormField label={t("leaveReferencePath")} hint={t("leaveReferencePathHint")} name="leaveReferencePath" dir="ltr" value={leaveReferencePath} onChange={(e) => setLeaveReferencePath(e.target.value)} />
              <FormField
                label={t("leaveIdempotencyHeader")}
                hint={t("leaveIdempotencyHeaderHint")}
                name="leaveIdempotencyHeader"
                dir="ltr"
                value={leaveIdempotencyHeader}
                onChange={(e) => setLeaveIdempotencyHeader(e.target.value)}
              />
            </>
          ) : null}
          {leaveBalancesPath.trim() || leaveSubmitPath.trim() ? (
            <TextAreaField label={t("leaveTypeCodes")} hint={t("leaveTypeCodesHint")} name="leaveTypeCodes" dir="ltr" value={leaveTypeCodes} onChange={(e) => setLeaveTypeCodes(e.target.value)} />
          ) : null}
        </fieldset>
        {mapping.render(t, t("pathsHint"))}
      </>
    ),
    values: (): Partial<HrRestSettings> => ({
      employeesPath: employeesPath.trim(),
      employeePath: blank(employeePath),
      employeeRecordPath: blank(employeePath) ? blank(employeeRecordPath) : null,
      payslipsPath: blank(payslipsPath),
      payslipRecordsPath: blank(payslipsPath) ? blank(payslipRecordsPath) : null,
      payslipFields: blank(payslipsPath)
        ? Object.fromEntries(Object.entries(payslipFields).map(([k, v]) => [k, v.trim()]).filter(([, v]) => v))
        : null,
      payslipPdfPath: blank(payslipsPath) ? blank(payslipPdfPath) : null,
      leaveBalancesPath: blank(leaveBalancesPath),
      leaveBalanceRecordsPath: blank(leaveBalancesPath) ? blank(leaveBalanceRecordsPath) : null,
      leaveBalanceFields: blank(leaveBalancesPath) ? { type: leaveType.trim(), balance: leaveBalance.trim() } : null,
      leaveSubmitPath: blank(leaveSubmitPath),
      leaveReferencePath: blank(leaveSubmitPath) ? blank(leaveReferencePath) : null,
      leaveIdempotencyHeader: blank(leaveSubmitPath) ? blank(leaveIdempotencyHeader) : null,
      leaveTypeCodes:
        blank(leaveBalancesPath) || blank(leaveSubmitPath)
          ? Object.fromEntries(
              leaveTypeCodes
                .split("\n")
                .map((line) => line.split("=").map((x) => x.trim()))
                .filter((p) => p.length === 2 && p[0] && p[1]),
            )
          : null,
      recordsPath: blank(recordsPath),
      paging,
      pageParam: paging === "page" ? blank(pageParam) : null,
      pageSizeParam: paging === "page" ? blank(pageSizeParam) : null,
      pageSize: paging === "page" ? Number(pageSize) || null : null,
      nextPath: paging === "next" ? blank(nextPath) : null,
      ...mapping.values(),
    }),
  };

  return <ConnectionSettingsForm initial={initial} submitLabel={submitLabel} busy={busy} onSubmit={onSubmit} authTypes={HR_AUTH} extra={extra} formLabel={t("restFormLabel")} />;
}

/**
 * The HR SFTP and CSV connection (spec 019 US1, FR-016): the SFTP server the HR system drops its full export on, with
 * a pinned host key and a secret manager reference for the credential, the CSV shape and which column holds each field.
 */
export function HrSftpSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: HrSftpBody) => void;
}) {
  const t = useTranslations("connections.hr");
  const tc = useTranslations("connections.clock");
  const ts = useTranslations("connections.settings");
  const ids = { auth: useId(), delimiter: useId() };
  const s = initial?.settings as Partial<HrSftpSettings> | undefined;
  const mapping = useMapping(s);
  const [name, setName] = useState(initial?.displayName ?? "");
  const [host, setHost] = useState(s?.sftp?.host ?? "");
  const [port, setPort] = useState(String(s?.sftp?.port ?? 22));
  const [username, setUsername] = useState(s?.sftp?.username ?? "");
  const [auth, setAuth] = useState<string>(s?.sftp?.authType ?? "password");
  const [secretRef, setSecretRef] = useState(s?.sftp?.secretRef ?? "");
  const [hostKey, setHostKey] = useState(s?.sftp?.hostKeySha256 ?? "");
  const [directory, setDirectory] = useState(s?.sftp?.directory ?? "");
  const [pattern, setPattern] = useState(s?.sftp?.filePattern ?? "*.csv");
  const [delimiter, setDelimiter] = useState<string>(s?.delimiter ?? ",");
  const [header, setHeader] = useState(s?.hasHeader ?? true);

  return (
    <form
      className="connections-form"
      aria-label={t("sftpFormLabel")}
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          displayName: name.trim(),
          settings: {
            sftp: {
              host: blank(host),
              port: Number(port) || null,
              username: blank(username),
              authType: auth,
              secretRef: blank(secretRef),
              hostKeySha256: blank(hostKey),
              directory: blank(directory),
              filePattern: blank(pattern),
            },
            delimiter,
            hasHeader: header,
            ...mapping.values(),
          },
        });
      }}
    >
      <FormField label={ts("name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <fieldset className="connections-fieldset">
        <legend>{tc("sftpTitle")}</legend>
        <FormField label={tc("host")} hint={tc("hostHint")} name="host" dir="ltr" required value={host} onChange={(e) => setHost(e.target.value)} />
        <FormField label={tc("port")} name="port" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} />
        <FormField label={tc("username")} name="username" dir="ltr" required value={username} onChange={(e) => setUsername(e.target.value)} />
        <div className="field">
          <label htmlFor={ids.auth}>{tc("authType")}</label>
          <select id={ids.auth} value={auth} onChange={(e) => setAuth(e.target.value)}>
            {CLOCK_AUTH.map((k) => (
              <option key={k} value={k}>
                {tc(`auth.${k}`)}
              </option>
            ))}
          </select>
        </div>
        <FormField label={ts("secretRef")} hint={ts("secretRefHint")} name="secretRef" dir="ltr" required value={secretRef} onChange={(e) => setSecretRef(e.target.value)} />
        <FormField label={tc("hostKey")} hint={tc("hostKeyHint")} name="hostKeySha256" dir="ltr" required value={hostKey} onChange={(e) => setHostKey(e.target.value)} />
        <FormField label={tc("directory")} hint={tc("directoryHint")} name="directory" dir="ltr" value={directory} onChange={(e) => setDirectory(e.target.value)} />
        <FormField label={tc("pattern")} hint={t("patternHint")} name="filePattern" dir="ltr" value={pattern} onChange={(e) => setPattern(e.target.value)} />
      </fieldset>
      <fieldset className="connections-fieldset">
        <legend>{tc("csvTitle")}</legend>
        <div className="field">
          <label htmlFor={ids.delimiter}>{tc("delimiter")}</label>
          <select id={ids.delimiter} value={delimiter} onChange={(e) => setDelimiter(e.target.value)}>
            {Object.entries(CLOCK_DELIMITERS).map(([k, v]) => (
              <option key={k} value={v}>
                {tc(`delimiters.${k}`)}
              </option>
            ))}
          </select>
        </div>
        <Checkbox label={tc("hasHeader")} checked={header} onChange={(e) => setHeader(e.target.checked)} />
      </fieldset>
      {mapping.render(t, tc(header ? "columnsByName" : "columnsByNumber"))}
      <div className="connections-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={ts("working")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
