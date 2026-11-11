"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField } from "@/components/ui/FormField";
import { CLOCK_AUTH, CLOCK_COLUMNS, CLOCK_DELIMITERS, CLOCK_SOURCES, type ClockColumns, type ClockSettings, type PackConnection } from "@/lib/integrations/types";

export type ClockBody = { displayName: string; settings: Partial<ClockSettings> };

const words = (text: string) =>
  text
    .split(",")
    .map((w) => w.trim())
    .filter(Boolean);

/**
 * Settings of a clock data connection (spec 017 US7): where files come from (an SFTP server read daily, or uploads),
 * the CSV shape and which column holds each field. The SFTP credential stays in the secret manager; only its
 * reference is entered. The API checks every value again.
 */
export function ClockSettingsForm({
  initial,
  submitLabel,
  busy,
  onSubmit,
}: {
  initial?: PackConnection;
  submitLabel: string;
  busy: boolean;
  onSubmit: (body: ClockBody) => void;
}) {
  const t = useTranslations("connections.clock");
  const ts = useTranslations("connections.settings");
  const ids = { source: useId(), auth: useId(), delimiter: useId() };
  const s = initial?.settings as Partial<ClockSettings> | undefined;
  const [name, setName] = useState(initial?.displayName ?? "");
  const [source, setSource] = useState<string>(s?.source ?? "sftp");
  const [host, setHost] = useState(s?.sftp?.host ?? "");
  const [port, setPort] = useState(String(s?.sftp?.port ?? 22));
  const [username, setUsername] = useState(s?.sftp?.username ?? "");
  const [auth, setAuth] = useState<string>(s?.sftp?.authType ?? "password");
  const [secretRef, setSecretRef] = useState(s?.sftp?.secretRef ?? "");
  const [hostKey, setHostKey] = useState(s?.sftp?.hostKeySha256 ?? "");
  const [directory, setDirectory] = useState(s?.sftp?.directory ?? "");
  const [pattern, setPattern] = useState(s?.sftp?.filePattern ?? "*.csv");
  const [importAt, setImportAt] = useState(s?.importAt ?? "02:00");
  const [delimiter, setDelimiter] = useState<string>(s?.delimiter ?? ",");
  const [header, setHeader] = useState(s?.hasHeader ?? true);
  const [columns, setColumns] = useState<Record<keyof ClockColumns, string>>(() => {
    const c = s?.columns;
    return Object.fromEntries(CLOCK_COLUMNS.map((k) => [k, c?.[k] ?? ""])) as Record<keyof ClockColumns, string>;
  });
  const [timestampFormat, setTimestampFormat] = useState(s?.timestampFormat ?? "");
  const [dateFormat, setDateFormat] = useState(s?.dateFormat ?? "");
  const [timeFormat, setTimeFormat] = useState(s?.timeFormat ?? "");
  const [timeZone, setTimeZone] = useState(s?.timeZone ?? "UTC");
  const [ins, setIns] = useState((s?.inValues ?? []).join(", "));
  const [outs, setOuts] = useState((s?.outValues ?? []).join(", "));

  const blank = (v: string) => (v.trim() ? v.trim() : null);

  return (
    <form
      className="connections-form"
      aria-label={t("formLabel")}
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          displayName: name.trim(),
          settings: {
            source: source as ClockSettings["source"],
            sftp:
              source === "sftp"
                ? {
                    host: blank(host),
                    port: Number(port) || null,
                    username: blank(username),
                    authType: auth,
                    secretRef: blank(secretRef),
                    hostKeySha256: blank(hostKey),
                    directory: blank(directory),
                    filePattern: blank(pattern),
                  }
                : null,
            importAt: source === "sftp" ? importAt : null,
            delimiter,
            hasHeader: header,
            columns: Object.fromEntries(CLOCK_COLUMNS.map((k) => [k, blank(columns[k])])) as ClockColumns,
            timestampFormat: blank(timestampFormat),
            dateFormat: blank(dateFormat),
            timeFormat: blank(timeFormat),
            timeZone: timeZone.trim(),
            inValues: words(ins),
            outValues: words(outs),
          },
        });
      }}
    >
      <FormField label={ts("name")} name="displayName" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      <div className="field">
        <label htmlFor={ids.source}>{t("source")}</label>
        <select id={ids.source} value={source} onChange={(e) => setSource(e.target.value)}>
          {CLOCK_SOURCES.map((k) => (
            <option key={k} value={k}>
              {t(`sources.${k}`)}
            </option>
          ))}
        </select>
      </div>

      {source === "sftp" ? (
        <fieldset className="connections-fieldset">
          <legend>{t("sftpTitle")}</legend>
          <FormField label={t("host")} hint={t("hostHint")} name="host" dir="ltr" required value={host} onChange={(e) => setHost(e.target.value)} />
          <FormField label={t("port")} name="port" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} />
          <FormField label={t("username")} name="username" dir="ltr" required value={username} onChange={(e) => setUsername(e.target.value)} />
          <div className="field">
            <label htmlFor={ids.auth}>{t("authType")}</label>
            <select id={ids.auth} value={auth} onChange={(e) => setAuth(e.target.value)}>
              {CLOCK_AUTH.map((k) => (
                <option key={k} value={k}>
                  {t(`auth.${k}`)}
                </option>
              ))}
            </select>
          </div>
          <FormField label={ts("secretRef")} hint={ts("secretRefHint")} name="secretRef" dir="ltr" required value={secretRef} onChange={(e) => setSecretRef(e.target.value)} />
          <FormField label={t("hostKey")} hint={t("hostKeyHint")} name="hostKeySha256" dir="ltr" required value={hostKey} onChange={(e) => setHostKey(e.target.value)} />
          <FormField label={t("directory")} hint={t("directoryHint")} name="directory" dir="ltr" value={directory} onChange={(e) => setDirectory(e.target.value)} />
          <FormField label={t("pattern")} hint={t("patternHint")} name="filePattern" dir="ltr" value={pattern} onChange={(e) => setPattern(e.target.value)} />
          <FormField label={t("importAt")} hint={t("importAtHint")} name="importAt" type="time" value={importAt} onChange={(e) => setImportAt(e.target.value)} />
        </fieldset>
      ) : null}

      <fieldset className="connections-fieldset">
        <legend>{t("csvTitle")}</legend>
        <div className="field">
          <label htmlFor={ids.delimiter}>{t("delimiter")}</label>
          <select id={ids.delimiter} value={delimiter} onChange={(e) => setDelimiter(e.target.value)}>
            {Object.entries(CLOCK_DELIMITERS).map(([k, v]) => (
              <option key={k} value={v}>
                {t(`delimiters.${k}`)}
              </option>
            ))}
          </select>
        </div>
        <Checkbox label={t("hasHeader")} checked={header} onChange={(e) => setHeader(e.target.checked)} />
        <p className="field-hint">{t(header ? "columnsByName" : "columnsByNumber")}</p>
        {CLOCK_COLUMNS.map((k) => (
          <FormField
            key={k}
            label={t(`columns.${k}`)}
            name={`column-${k}`}
            dir="ltr"
            required={k === "employeeNumber"}
            value={columns[k]}
            onChange={(e) => setColumns((c) => ({ ...c, [k]: e.target.value }))}
          />
        ))}
        <FormField label={t("timestampFormat")} hint={t("formatHint")} name="timestampFormat" dir="ltr" value={timestampFormat} onChange={(e) => setTimestampFormat(e.target.value)} />
        <FormField label={t("dateFormat")} name="dateFormat" dir="ltr" value={dateFormat} onChange={(e) => setDateFormat(e.target.value)} />
        <FormField label={t("timeFormat")} name="timeFormat" dir="ltr" value={timeFormat} onChange={(e) => setTimeFormat(e.target.value)} />
        <FormField label={t("timeZone")} hint={t("timeZoneHint")} name="timeZone" dir="ltr" required value={timeZone} onChange={(e) => setTimeZone(e.target.value)} />
        <FormField label={t("inValues")} hint={t("valuesHint")} name="inValues" dir="ltr" value={ins} onChange={(e) => setIns(e.target.value)} />
        <FormField label={t("outValues")} hint={t("valuesHint")} name="outValues" dir="ltr" value={outs} onChange={(e) => setOuts(e.target.value)} />
      </fieldset>
      <div className="connections-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={ts("working")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
