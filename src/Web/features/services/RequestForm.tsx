"use client";

import { useId, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages, type Problem } from "@/lib/notifications/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { isShown, pick, type AttachmentView, type FieldValue, type FormDefinition, type FormField } from "@/lib/services/types";
import { PersonPicker } from "./PersonPicker";
import "./services.css";

type Target = { kind: "new"; serviceId: string; preparedId?: string } | { kind: "resubmit"; requestId: string } | { kind: "preview" };

type Upload = { id: string; fileName: string; state: AttachmentView["scanState"] };

/**
 * A request form (spec 016 US2, FR-003, FR-004): the published fields of a service, prefilled from the person's
 * profile, with directory values read only. Conditions only show or hide fields; every rule is checked again by the API,
 * which also puts read-only values back from their source. Attachments are scanned on upload and only clean files can
 * be attached.
 */
export function RequestForm({
  target,
  form,
  initial,
  readOnly,
  units,
  existingAttachments = [],
  allowOnBehalf = false,
}: {
  target: Target;
  form: FormDefinition;
  initial: Record<string, FieldValue>;
  readOnly: string[];
  units: OrgUnit[];
  existingAttachments?: AttachmentView[];
  allowOnBehalf?: boolean;
}) {
  const t = useTranslations("services.form");
  const locale = useLocale();
  const router = useRouter();
  const formId = useId();
  const [data, setData] = useState<Record<string, FieldValue>>(initial);
  const [uploads, setUploads] = useState<Record<string, Upload[]>>(() => {
    const byField: Record<string, Upload[]> = {};
    for (const f of form.fields.filter((x) => x.type === "attachment")) {
      const ids = Array.isArray(initial[f.key]) ? (initial[f.key] as string[]) : [];
      byField[f.key] = existingAttachments.filter((a) => ids.includes(a.id)).map((a) => ({ id: a.id, fileName: a.fileName, state: a.scanState }));
    }
    return byField;
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [general, setGeneral] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [idempotencyKey] = useState(() => (typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`));
  const [lockedKeys, setLockedKeys] = useState(readOnly);
  const locked = useMemo(() => new Set(lockedKeys), [lockedKeys]);
  const [forWhom, setForWhom] = useState<string | null>(null);

  /** Filing for someone else: their profile fills the form, and the API checks the permission at their unit. */
  async function fileFor(person: string | null) {
    if (target.kind !== "new") return;
    setForWhom(person);
    const query = person ? `?for=${encodeURIComponent(person)}` : "";
    try {
      const response = await fetch(`/api/v1/services/${target.serviceId}/prefill${query}`, { cache: "no-store" });
      if (response.ok) {
        const prefill = (await response.json()) as { values: Record<string, FieldValue>; readOnly: string[] };
        setData((d) => ({ ...Object.fromEntries(Object.entries(d).filter(([k]) => !locked.has(k))), ...prefill.values }));
        setLockedKeys(prefill.readOnly);
        setGeneral([]);
      } else {
        setGeneral(await problemMessages(response));
      }
    } catch {
      setGeneral([t("failed")]);
    }
  }

  const set = (key: string, value: FieldValue) => setData((d) => ({ ...d, [key]: value }));
  const label = (f: FormField) => pick(f.labels, locale, f.key);

  async function upload(field: FormField, files: FileList | null) {
    if (!files) return;
    for (const file of Array.from(files)) {
      try {
        const response = await fetch(`/api/v1/requests/attachments?name=${encodeURIComponent(file.name)}`, {
          method: "POST",
          headers: { "content-type": file.type || "application/octet-stream" },
          body: file,
        });
        if (!response.ok) {
          const messages = await problemMessages(response);
          setErrors((e) => ({ ...e, [field.key]: messages[0] ?? t("uploadFailed") }));
          continue;
        }
        const saved = (await response.json()) as AttachmentView;
        setUploads((u) => ({ ...u, [field.key]: [...(u[field.key] ?? []), { id: saved.id, fileName: saved.fileName, state: saved.scanState }] }));
        if (saved.scanState === "clean") {
          setData((d) => ({ ...d, [field.key]: [...(Array.isArray(d[field.key]) ? (d[field.key] as string[]) : []), saved.id] }));
        }
      } catch {
        setErrors((e) => ({ ...e, [field.key]: t("uploadFailed") }));
      }
    }
  }

  function removeUpload(field: FormField, id: string) {
    setUploads((u) => ({ ...u, [field.key]: (u[field.key] ?? []).filter((x) => x.id !== id) }));
    setData((d) => ({ ...d, [field.key]: (Array.isArray(d[field.key]) ? (d[field.key] as string[]) : []).filter((x) => x !== id) }));
  }

  async function submit() {
    if (target.kind === "preview") return;
    setBusy(true);
    setErrors({});
    setGeneral([]);
    const shown: Record<string, FieldValue> = {};
    for (const f of form.fields) {
      if (f.type !== "policy_note" && isShown(form, f, data) && data[f.key] !== undefined && data[f.key] !== "") shown[f.key] = data[f.key]!;
    }
    try {
      const response =
        target.kind === "new"
          ? await fetch("/api/v1/requests", {
              method: "POST",
              headers: { "content-type": "application/json" },
              body: JSON.stringify({ serviceId: target.serviceId, data: shown, idempotencyKey, onBehalfOf: forWhom, preparedId: target.preparedId }),
            })
          : await fetch(`/api/v1/requests/${target.requestId}/resubmit`, {
              method: "POST",
              headers: { "content-type": "application/json" },
              body: JSON.stringify({ data: shown }),
            });
      if (response.ok) {
        const detail = (await response.json()) as { request: { id: string } };
        router.push(`/requests/${detail.request.id}`);
        router.refresh();
        return;
      }
      const copy = response.clone();
      let problem: Problem = {};
      try {
        problem = (await copy.json()) as Problem;
      } catch {
        // Not a problem document; the general message below covers it.
      }
      const byField: Record<string, string> = {};
      const rest: string[] = [];
      for (const [path, list] of Object.entries(problem.errors ?? {})) {
        const key = path.startsWith("data.") ? path.slice(5) : null;
        const message = list.map((e) => e.message).join(" ");
        if (key && form.fields.some((f) => f.key === key)) byField[key] = message;
        else rest.push(message);
      }
      setErrors(byField);
      setGeneral(rest.length > 0 || Object.keys(byField).length > 0 ? rest : await problemMessages(response));
    } catch {
      setGeneral([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const describedBy = (f: FormField) => [f.help ? `${formId}-${f.key}-help` : null, errors[f.key] ? `${formId}-${f.key}-error` : null].filter(Boolean).join(" ") || undefined;

  function control(f: FormField) {
    const id = `${formId}-${f.key}`;
    const value = data[f.key];
    const isLocked = locked.has(f.key);
    const common = { id, "aria-describedby": describedBy(f), "aria-invalid": errors[f.key] ? true : undefined, required: f.required || undefined };
    switch (f.type) {
      case "text":
        return <input {...common} type="text" readOnly={isLocked} value={typeof value === "string" ? value : ""} onChange={(e) => set(f.key, e.target.value)} />;
      case "number":
        return (
          <input
            {...common}
            type="number"
            inputMode="decimal"
            readOnly={isLocked}
            value={typeof value === "number" ? value : ""}
            onChange={(e) => set(f.key, e.target.value === "" ? null : Number(e.target.value))}
          />
        );
      case "money": {
        const money = value && typeof value === "object" && "amount" in value ? value : { amount: Number.NaN, currency: f.currencies?.[0] ?? "" };
        return (
          <div className="services-row">
            <input
              {...common}
              type="number"
              inputMode="decimal"
              min={0}
              value={Number.isNaN(money.amount) ? "" : money.amount}
              onChange={(e) => set(f.key, e.target.value === "" ? null : { amount: Number(e.target.value), currency: money.currency })}
            />
            <select
              aria-label={t("currency", { field: label(f) })}
              value={money.currency}
              onChange={(e) => set(f.key, { amount: Number.isNaN(money.amount) ? 0 : money.amount, currency: e.target.value })}
            >
              {(f.currencies ?? []).map((c) => (
                <option key={c} value={c}>
                  {c}
                </option>
              ))}
            </select>
          </div>
        );
      }
      case "date":
        return <input {...common} type="date" readOnly={isLocked} value={typeof value === "string" ? value : ""} onChange={(e) => set(f.key, e.target.value || null)} />;
      case "date_range": {
        const range = value && typeof value === "object" && "from" in value ? value : { from: "", to: "" };
        return (
          <div className="services-row">
            <label className="services-select">
              {t("from")}
              <input {...common} type="date" value={range.from} onChange={(e) => set(f.key, { ...range, from: e.target.value })} />
            </label>
            <label className="services-select">
              {t("to")}
              <input type="date" value={range.to} onChange={(e) => set(f.key, { ...range, to: e.target.value })} />
            </label>
          </div>
        );
      }
      case "choice":
      case "lookup":
        return (
          <select {...common} disabled={isLocked} value={typeof value === "string" ? value : ""} onChange={(e) => set(f.key, e.target.value || null)}>
            <option value="">{t("choose")}</option>
            {(f.options ?? []).map((o) => (
              <option key={o.value} value={o.value}>
                {pick(o.labels, locale, o.value)}
              </option>
            ))}
          </select>
        );
      case "org_unit":
        return (
          <select {...common} disabled={isLocked} value={typeof value === "string" ? value : ""} onChange={(e) => set(f.key, e.target.value || null)}>
            <option value="">{t("choose")}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {unitName(u, locale)}
              </option>
            ))}
          </select>
        );
      default:
        return null;
    }
  }

  function field(f: FormField) {
    if (!isShown(form, f, data)) return null;
    const help = f.help ? (
      <p id={`${formId}-${f.key}-help`} className="field-hint">
        {pick(f.help, locale)}
      </p>
    ) : null;
    const error = errors[f.key] ? (
      <p id={`${formId}-${f.key}-error`} className="field-error" role="alert">
        {errors[f.key]}
      </p>
    ) : null;
    const lockedHint = locked.has(f.key) ? <p className="field-hint">{t("fromProfile")}</p> : null;
    if (f.type === "policy_note") {
      return (
        <div key={f.key} className="services-note" role="note">
          <strong>{label(f)}</strong>
          <p>{pick(f.note, locale)}</p>
        </div>
      );
    }
    if (f.type === "person") {
      const value = Array.isArray(data[f.key]) ? ((data[f.key] as string[])[0] ?? null) : typeof data[f.key] === "string" ? (data[f.key] as string) : null;
      return (
        <div key={f.key}>
          <PersonPicker label={label(f)} value={value} readOnly={locked.has(f.key)} onChange={(id) => set(f.key, id)} error={errors[f.key]} />
          {lockedHint}
        </div>
      );
    }
    if (f.type === "multi_choice") {
      const values = Array.isArray(data[f.key]) ? (data[f.key] as string[]) : [];
      return (
        <fieldset key={f.key} aria-describedby={describedBy(f)}>
          <legend>
            {label(f)}
            {f.required ? <span aria-hidden="true">{t("requiredMark")}</span> : null}
          </legend>
          {help}
          {(f.options ?? []).map((o) => (
            <label key={o.value} className="services-check">
              <input
                type="checkbox"
                checked={values.includes(o.value)}
                onChange={(e) => set(f.key, e.target.checked ? [...values, o.value] : values.filter((v) => v !== o.value))}
              />
              {pick(o.labels, locale, o.value)}
            </label>
          ))}
          {error}
        </fieldset>
      );
    }
    if (f.type === "attachment") {
      const list = uploads[f.key] ?? [];
      return (
        <fieldset key={f.key} aria-describedby={describedBy(f)}>
          <legend>
            {label(f)}
            {f.required ? <span aria-hidden="true">{t("requiredMark")}</span> : null}
          </legend>
          {help}
          <label className="services-select">
            {t("addFiles")}
            <input type="file" multiple onChange={(e) => void upload(f, e.target.files)} />
          </label>
          <ul className="services-list" aria-live="polite">
            {list.map((u) => (
              <li key={u.id} className="services-row">
                <bdi>{u.fileName}</bdi>
                <span className="services-status">{t(`scan.${u.state}`)}</span>
                <Button variant="quiet" onClick={() => removeUpload(f, u.id)} aria-label={t("removeFile", { name: u.fileName })}>
                  {t("remove")}
                </Button>
              </li>
            ))}
          </ul>
          {error}
        </fieldset>
      );
    }
    return (
      <div key={f.key} className="field">
        <label htmlFor={`${formId}-${f.key}`}>
          {label(f)}
          {f.required ? <span aria-hidden="true">{t("requiredMark")}</span> : null}
        </label>
        {help}
        {control(f)}
        {lockedHint}
        {error}
      </div>
    );
  }

  return (
    <form
      className="services-form services-section"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
    >
      {allowOnBehalf && target.kind === "new" ? (
        <div>
          <PersonPicker label={t("onBehalf")} value={forWhom} onChange={(id) => void fileFor(id)} />
          <p className="field-hint">{t("onBehalfHint")}</p>
        </div>
      ) : null}
      {form.fields.map(field)}
      <div aria-live="polite">
        {general.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {general.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>
      <div className="services-actions">
        {target.kind === "preview" ? (
          <p className="field-hint">{t("previewOnly")}</p>
        ) : (
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("submitting")}>
            {target.kind === "new" ? t("submit") : t("resubmit")}
          </Button>
        )}
      </div>
    </form>
  );
}
