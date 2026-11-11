"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import {
  fieldTypes,
  pick,
  isHrSource,
  prefillSourcesFor,
  type ActionStep,
  type ApprovalStep,
  type Category,
  type FieldType,
  type FormField as Field,
  type Names,
  type RouteActionsView,
  type ServiceAdminView,
  type ServicePreview,
} from "@/lib/services/types";
import { ActionStepsEditor } from "./ActionStepsEditor";
import { PersonPicker } from "./PersonPicker";
import { RequestForm } from "./RequestForm";
import "./services.css";

const membershipTypes = ["employee", "contractor", "guest"] as const;
const classifications = ["public", "internal", "confidential", "restricted"] as const;
const withOptions = new Set<FieldType>(["choice", "multi_choice", "lookup"]);
const withRange = new Set<FieldType>(["number", "money"]);
const withPrefill = new Set<FieldType>(["text", "person", "org_unit", "date"]);

type Result = { ok: boolean; messages: string[] } | null;

function Notice({ result }: { result: Result }) {
  if (!result) return null;
  return (
    <div className={result.ok ? "notice" : "notice notice-error"} role={result.ok ? "status" : "alert"}>
      {result.messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  );
}

function BilingualFields({ label, value, onChange, name }: { label: string; value: Names | null | undefined; onChange: (v: Names) => void; name: string }) {
  const t = useTranslations("services.admin");
  return (
    <div className="services-row">
      <FormField label={t("inEnglish", { label })} name={`${name}-en`} dir="ltr" lang="en" value={value?.en ?? ""} onChange={(e) => onChange({ ...value, en: e.target.value })} />
      <FormField label={t("inArabic", { label })} name={`${name}-ar`} dir="rtl" lang="ar" value={value?.ar ?? ""} onChange={(e) => onChange({ ...value, ar: e.target.value })} />
    </div>
  );
}

/** Drops empty languages so the API gets only the texts the owner wrote. */
function clean(names: Names | null | undefined): Names | null {
  if (!names) return null;
  const kept = Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim() !== ""));
  return Object.keys(kept).length > 0 ? kept : null;
}

/**
 * The basic service builder (spec 016 US7, R1): settings with names in Arabic and English, category, audience and
 * owner; for form services, the form fields, one approval step, a preview as a requester sees it, and publish. Every
 * rule is checked by the API, which also keeps the audience inside the owner's unit.
 */
export function ServiceEditor({
  initial,
  categories,
  units,
  routeActions,
}: {
  initial: ServiceAdminView;
  categories: Category[];
  units: OrgUnit[];
  routeActions: RouteActionsView;
}) {
  const t = useTranslations("services.admin");
  const locale = useLocale();
  const router = useRouter();
  const [view, setView] = useState(initial);
  const s = view.service;
  const [names, setNames] = useState<Names>(s.names);
  const [descriptions, setDescriptions] = useState<Names>(s.descriptions);
  const [categoryId, setCategoryId] = useState(s.categoryId ?? "");
  const [owner, setOwner] = useState(s.ownerOrgUnitId ?? "");
  const [audienceUnits, setAudienceUnits] = useState<string[]>(s.audience.orgUnits ?? []);
  const [audienceTypes, setAudienceTypes] = useState<string[]>(s.audience.membershipTypes ?? []);
  const [classification, setClassification] = useState(s.classification);
  const [url, setUrl] = useState(s.url ?? "");
  const [sso, setSso] = useState(s.sso);
  const [settingsResult, setSettingsResult] = useState<Result>(null);

  const latest = view.latest;
  const [fields, setFields] = useState<Field[]>(latest?.form.fields ?? []);
  const [approval, setApproval] = useState<ApprovalStep | null>(latest?.route.approval ?? null);
  const [actionSteps, setActionSteps] = useState<ActionStep[]>(latest?.route.actions ?? []);
  const [allowOnBehalf, setAllowOnBehalf] = useState(latest?.allowOnBehalf ?? false);
  const [maxAttachments, setMaxAttachments] = useState(latest?.maxAttachments ?? 0);
  const [draftResult, setDraftResult] = useState<Result>(null);
  const [preview, setPreview] = useState<ServicePreview | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  async function call(path: string, method: string, body?: unknown): Promise<Response> {
    return fetch(`/api/v1/services/admin/${s.id}${path}`, {
      method,
      headers: body === undefined ? undefined : { "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  }

  async function saveSettings() {
    setBusy("settings");
    setSettingsResult(null);
    try {
      const response = await call("", "PUT", {
        names: clean(names),
        descriptions: clean(descriptions),
        categoryId: categoryId || null,
        ownerOrgUnitId: owner || null,
        audience: { orgUnits: audienceUnits, membershipTypes: audienceTypes },
        classification,
        url: s.kind === "linked" ? url : null,
        sso,
      });
      if (response.ok) {
        setView((await response.json()) as ServiceAdminView);
        setSettingsResult({ ok: true, messages: [t("saved")] });
      } else {
        setSettingsResult({ ok: false, messages: await problemMessages(response) });
      }
    } finally {
      setBusy(null);
    }
  }

  async function saveDraft(): Promise<boolean> {
    setBusy("draft");
    setDraftResult(null);
    try {
      const response = await call("/draft", "PUT", {
        form: {
          fields: fields.map((f) => ({
            ...f,
            labels: clean(f.labels) ?? {},
            help: clean(f.help),
            note: clean(f.note),
            prefill: f.prefill || null,
            readOnly: isHrSource(f.prefill) ? (f.readOnly ?? false) : false,
            mustBeCurrent: isHrSource(f.prefill) ? (f.mustBeCurrent ?? false) : false,
            options: withOptions.has(f.type) ? (f.options ?? []).map((o) => ({ value: o.value, labels: clean(o.labels) ?? {} })) : null,
            rules: f.rules && f.rules.length > 0 ? f.rules : null,
            showWhen: f.showWhen && f.showWhen.field ? f.showWhen : null,
          })),
        },
        route: {
          approval: approval ? { ...approval, names: clean(approval.names) } : null,
          actions: actionSteps.length > 0 ? actionSteps.map((a) => ({ ...a, names: clean(a.names) })) : null,
        },
        allowOnBehalf,
        maxAttachments,
      });
      if (response.ok) {
        setView((await response.json()) as ServiceAdminView);
        setDraftResult({ ok: true, messages: [t("draftSaved")] });
        return true;
      }
      setDraftResult({ ok: false, messages: await problemMessages(response) });
      return false;
    } finally {
      setBusy(null);
    }
  }

  async function showPreview() {
    if (!(await saveDraft())) return;
    const response = await call("/preview", "GET");
    if (response.ok) setPreview((await response.json()) as ServicePreview);
  }

  async function publish() {
    if (s.kind === "form" && !(await saveDraft())) return;
    setBusy("publish");
    try {
      const response = await call("/publish", "POST", {});
      if (response.ok) {
        setView((await response.json()) as ServiceAdminView);
        setDraftResult({ ok: true, messages: [t("published")] });
        router.refresh();
      } else {
        setDraftResult({ ok: false, messages: await problemMessages(response) });
      }
    } finally {
      setBusy(null);
    }
  }

  async function retire() {
    const response = await call("/retire", "POST", {});
    if (response.ok) {
      setView((await response.json()) as ServiceAdminView);
      setDraftResult({ ok: true, messages: [t("retired")] });
    } else {
      setDraftResult({ ok: false, messages: await problemMessages(response) });
    }
  }

  const update = (i: number, change: Partial<Field>) => setFields(fields.map((f, j) => (j === i ? { ...f, ...change } : f)));
  const move = (i: number, by: number) => {
    const next = [...fields];
    const [item] = next.splice(i, 1);
    next.splice(i + by, 0, item!);
    setFields(next);
  };
  const ruleValue = (f: Field, kind: string) => f.rules?.find((r) => r.kind === kind)?.value ?? "";
  const setRule = (i: number, kind: string, raw: string) => {
    const f = fields[i]!;
    const others = (f.rules ?? []).filter((r) => r.kind !== kind);
    update(i, { rules: raw === "" ? others : [...others, { kind, value: Number(raw) }] });
  };
  const choiceFields = (i: number) => fields.slice(0, i).filter((f) => f.type === "choice" || f.type === "multi_choice");

  function fieldEditor(f: Field, i: number) {
    const title = pick(f.labels, locale, f.key || t("newField"));
    return (
      <fieldset key={i} className="services-builder-field" data-testid="builder-field">
        <legend>{t("fieldTitle", { position: i + 1, name: title })}</legend>
        <div className="services-row">
          <FormField label={t("fieldKey")} name={`key-${i}`} dir="ltr" hint={t("fieldKeyHint")} value={f.key} onChange={(e) => update(i, { key: e.target.value })} />
          <label className="services-select">
            {t("fieldType")}
            <select value={f.type} onChange={(e) => update(i, { type: e.target.value as FieldType })}>
              {fieldTypes.map((type) => (
                <option key={type} value={type}>
                  {t(`type.${type}`)}
                </option>
              ))}
            </select>
          </label>
        </div>
        <BilingualFields label={t("fieldLabel")} name={`label-${i}`} value={f.labels} onChange={(labels) => update(i, { labels })} />
        {f.type === "policy_note" ? (
          <BilingualFields label={t("note")} name={`note-${i}`} value={f.note} onChange={(note) => update(i, { note })} />
        ) : (
          <>
            <BilingualFields label={t("help")} name={`help-${i}`} value={f.help} onChange={(help) => update(i, { help })} />
            <label className="services-check">
              <input type="checkbox" checked={f.required ?? false} onChange={(e) => update(i, { required: e.target.checked })} />
              {t("required")}
            </label>
          </>
        )}
        {withPrefill.has(f.type) ? (
          <label className="services-select">
            {t("prefill")}
            <select value={f.prefill ?? ""} onChange={(e) => update(i, { prefill: e.target.value || null })}>
              <option value="">{t("noPrefill")}</option>
              {prefillSourcesFor(f.type).map((p) => (
                <option key={p} value={p}>
                  {t(`source.${p.replace(".", "_")}`)}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        {withPrefill.has(f.type) && isHrSource(f.prefill) ? (
          <>
            <p className="field-hint">{t("hrPrefillHint")}</p>
            <label className="services-check">
              <input type="checkbox" checked={f.readOnly ?? false} onChange={(e) => update(i, { readOnly: e.target.checked })} />
              {t("hrReadOnly")}
            </label>
            <label className="services-check">
              <input type="checkbox" checked={f.mustBeCurrent ?? false} onChange={(e) => update(i, { mustBeCurrent: e.target.checked })} />
              {t("hrMustBeCurrent")}
            </label>
          </>
        ) : null}
        {withRange.has(f.type) ? (
          <div className="services-row">
            <FormField label={t("min")} name={`min-${i}`} type="number" value={ruleValue(f, "min")} onChange={(e) => setRule(i, "min", e.target.value)} />
            <FormField label={t("max")} name={`max-${i}`} type="number" value={ruleValue(f, "max")} onChange={(e) => setRule(i, "max", e.target.value)} />
          </div>
        ) : null}
        {f.type === "date" || f.type === "date_range" ? (
          <FormField
            label={t("minDays")}
            name={`mindays-${i}`}
            type="number"
            hint={t("minDaysHint")}
            value={ruleValue(f, "min_days_from_today")}
            onChange={(e) => setRule(i, "min_days_from_today", e.target.value)}
          />
        ) : null}
        {f.type === "money" ? (
          <FormField
            label={t("currencies")}
            name={`currencies-${i}`}
            dir="ltr"
            hint={t("currenciesHint")}
            value={(f.currencies ?? []).join(", ")}
            onChange={(e) => update(i, { currencies: e.target.value.split(",").map((c) => c.trim().toUpperCase()).filter(Boolean) })}
          />
        ) : null}
        {withOptions.has(f.type) ? (
          <div className="services-list">
            {(f.options ?? []).map((o, k) => (
              <div key={k} className="services-row">
                <FormField
                  label={t("optionValue", { position: k + 1 })}
                  name={`option-${i}-${k}`}
                  dir="ltr"
                  value={o.value}
                  onChange={(e) => update(i, { options: (f.options ?? []).map((x, m) => (m === k ? { ...x, value: e.target.value } : x)) })}
                />
                <BilingualFields
                  label={t("optionLabel", { position: k + 1 })}
                  name={`option-label-${i}-${k}`}
                  value={o.labels}
                  onChange={(labels) => update(i, { options: (f.options ?? []).map((x, m) => (m === k ? { ...x, labels } : x)) })}
                />
                <Button variant="quiet" onClick={() => update(i, { options: (f.options ?? []).filter((_, m) => m !== k) })}>
                  {t("removeOption")}
                </Button>
              </div>
            ))}
            <div>
              <Button onClick={() => update(i, { options: [...(f.options ?? []), { value: "", labels: {} }] })}>{t("addOption")}</Button>
            </div>
          </div>
        ) : null}
        {choiceFields(i).length > 0 && f.type !== "policy_note" ? (
          <div className="services-row">
            <label className="services-select">
              {t("showWhen")}
              <select
                value={f.showWhen?.field ?? ""}
                onChange={(e) => update(i, { showWhen: e.target.value ? { field: e.target.value, values: f.showWhen?.values ?? [] } : null })}
              >
                <option value="">{t("always")}</option>
                {choiceFields(i).map((c) => (
                  <option key={c.key} value={c.key}>
                    {pick(c.labels, locale, c.key)}
                  </option>
                ))}
              </select>
            </label>
            {f.showWhen?.field ? (
              <FormField
                label={t("showWhenValues")}
                name={`when-${i}`}
                dir="ltr"
                hint={t("showWhenHint")}
                value={f.showWhen.values.join(", ")}
                onChange={(e) => update(i, { showWhen: { field: f.showWhen!.field, values: e.target.value.split(",").map((v) => v.trim()).filter(Boolean) } })}
              />
            ) : null}
          </div>
        ) : null}
        <div className="services-actions">
          <Button variant="quiet" disabled={i === 0} onClick={() => move(i, -1)}>
            {t("moveUp")}
          </Button>
          <Button variant="quiet" disabled={i === fields.length - 1} onClick={() => move(i, 1)}>
            {t("moveDown")}
          </Button>
          <Button variant="destructive" onClick={() => setFields(fields.filter((_, j) => j !== i))}>
            {t("removeField")}
          </Button>
        </div>
      </fieldset>
    );
  }

  return (
    <div className="services">
      <p className="services-row">
        <span className="services-status" data-testid="service-state">
          {t(`state.${s.status}`)}
        </span>
        {latest ? <span className="services-meta">{t("versionState", { version: latest.version, state: t(`versionStates.${latest.state}`) })}</span> : null}
      </p>

      <form
        className="services-section services-form"
        aria-labelledby="service-settings"
        onSubmit={(e) => {
          e.preventDefault();
          void saveSettings();
        }}
      >
        <h2 id="service-settings">{t("settings")}</h2>
        <BilingualFields label={t("name")} name="name" value={names} onChange={setNames} />
        <BilingualFields label={t("description")} name="description" value={descriptions} onChange={setDescriptions} />
        {s.kind === "linked" ? (
          <>
            <FormField label={t("url")} name="url" type="url" dir="ltr" value={url} onChange={(e) => setUrl(e.target.value)} />
            <label className="services-check">
              <input type="checkbox" checked={sso} onChange={(e) => setSso(e.target.checked)} />
              {t("sso")}
            </label>
          </>
        ) : null}
        <div className="services-row">
          <label className="services-select">
            {t("category")}
            <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              <option value="">{t("noCategory")}</option>
              {categories.map((c) => (
                <option key={c.id} value={c.id}>
                  {pick(c.names, locale)}
                </option>
              ))}
            </select>
          </label>
          <label className="services-select">
            {t("owner")}
            <select value={owner} onChange={(e) => setOwner(e.target.value)}>
              <option value="">{t("ownerTenant")}</option>
              {units.map((u) => (
                <option key={u.id} value={u.id}>
                  {unitName(u, locale)}
                </option>
              ))}
            </select>
          </label>
          <label className="services-select">
            {t("classification")}
            <select value={classification} onChange={(e) => setClassification(e.target.value)}>
              {classifications.map((c) => (
                <option key={c} value={c}>
                  {t(`classifications.${c}`)}
                </option>
              ))}
            </select>
          </label>
        </div>
        <fieldset>
          <legend>{t("audience")}</legend>
          <p className="field-hint">{t("audienceHint")}</p>
          <label className="services-select">
            {t("audienceUnits")}
            <select multiple value={audienceUnits} onChange={(e) => setAudienceUnits(Array.from(e.target.selectedOptions).map((o) => o.value))}>
              {units.map((u) => (
                <option key={u.id} value={u.id}>
                  {unitName(u, locale)}
                </option>
              ))}
            </select>
          </label>
          {membershipTypes.map((m) => (
            <label key={m} className="services-check">
              <input
                type="checkbox"
                checked={audienceTypes.includes(m)}
                onChange={(e) => setAudienceTypes(e.target.checked ? [...audienceTypes, m] : audienceTypes.filter((x) => x !== m))}
              />
              {t(`membership.${m}`)}
            </label>
          ))}
        </fieldset>
        <Notice result={settingsResult} />
        <div className="services-actions">
          <Button type="submit" busy={busy === "settings"} busyLabel={t("saving")}>
            {t("saveSettings")}
          </Button>
        </div>
      </form>

      {s.kind === "form" ? (
        <form
          className="services-section services-form"
          aria-labelledby="service-form"
          onSubmit={(e) => {
            e.preventDefault();
            void saveDraft();
          }}
        >
          <h2 id="service-form">{t("formTitle")}</h2>
          {fields.map(fieldEditor)}
          <div>
            <Button onClick={() => setFields([...fields, { key: `field_${fields.length + 1}`, type: "text", labels: {}, required: false }])}>{t("addField")}</Button>
          </div>
          <label className="services-check">
            <input type="checkbox" checked={allowOnBehalf} onChange={(e) => setAllowOnBehalf(e.target.checked)} />
            {t("allowOnBehalf")}
          </label>
          <FormField label={t("maxAttachments")} name="maxAttachments" type="number" min={0} max={10} value={maxAttachments} onChange={(e) => setMaxAttachments(Number(e.target.value || 0))} />

          <fieldset>
            <legend>{t("approval")}</legend>
            <label className="services-select">
              {t("approvalKind")}
              <select
                value={approval ? approval.approvers.kind : ""}
                onChange={(e) =>
                  setApproval(
                    e.target.value === ""
                      ? null
                      : {
                          names: approval?.names ?? null,
                          dueWorkingDays: approval?.dueWorkingDays ?? null,
                          approvers:
                            e.target.value === "users"
                              ? { kind: "users", users: [] }
                              : e.target.value === "line_manager"
                                ? { kind: "line_manager", permission: null }
                                : { kind: "permission_at_unit", permission: "workflow.approve" },
                        },
                  )
                }
              >
                <option value="">{t("noApproval")}</option>
                <option value="users">{t("approvers.users")}</option>
                <option value="permission_at_unit">{t("approvers.permission_at_unit")}</option>
                <option value="line_manager">{t("approvers.line_manager")}</option>
              </select>
            </label>
            {approval ? (
              <>
                <BilingualFields label={t("stepName")} name="step" value={approval.names} onChange={(n) => setApproval({ ...approval, names: n })} />
                {approval.approvers.kind === "users" ? (
                  <>
                    <ul className="services-list" aria-label={t("approverList")}>
                      {(approval.approvers.users ?? []).map((u) => (
                        <li key={u} className="services-row">
                          <PersonPicker label={t("approver")} value={u} readOnly onChange={() => undefined} />
                          <Button
                            variant="quiet"
                            onClick={() => setApproval({ ...approval, approvers: { kind: "users", users: (approval.approvers.users ?? []).filter((x) => x !== u) } })}
                          >
                            {t("removeApprover")}
                          </Button>
                        </li>
                      ))}
                    </ul>
                    <PersonPicker
                      label={t("addApprover")}
                      value={null}
                      onChange={(id) =>
                        id && setApproval({ ...approval, approvers: { kind: "users", users: [...new Set([...(approval.approvers.users ?? []), id])] } })
                      }
                    />
                  </>
                ) : approval.approvers.kind === "line_manager" ? (
                  <p className="field-hint">{t("lineManagerHint")}</p>
                ) : (
                  <p className="field-hint">{t("permissionHint")}</p>
                )}
                <FormField
                  label={t("dueDays")}
                  name="dueDays"
                  type="number"
                  min={1}
                  max={90}
                  hint={t("dueDaysHint")}
                  value={approval.dueWorkingDays ?? ""}
                  onChange={(e) => setApproval({ ...approval, dueWorkingDays: e.target.value === "" ? null : Number(e.target.value) })}
                />
              </>
            ) : null}
          </fieldset>
          {routeActions.available ? (
            <ActionStepsEditor
              steps={actionSteps}
              actions={routeActions.actions}
              fields={fields.filter((f) => f.type !== "policy_note").map((f) => ({ key: f.key, label: pick(f.labels, locale, f.key) }))}
              onChange={setActionSteps}
            />
          ) : null}
          <Notice result={draftResult} />
          <div className="services-actions">
            <Button type="submit" busy={busy === "draft"} busyLabel={t("saving")}>
              {t("saveDraft")}
            </Button>
            <Button onClick={() => void showPreview()}>{t("preview")}</Button>
            <Button variant="primary" busy={busy === "publish"} busyLabel={t("publishing")} onClick={() => void publish()}>
              {t("publish")}
            </Button>
            {s.status === "published" ? (
              <ConfirmAction label={t("retire")} question={t("retireQuestion")} confirmLabel={t("retireConfirm")} cancelLabel={t("keep")} onConfirm={retire} />
            ) : null}
          </div>
        </form>
      ) : (
        <section className="services-section" aria-labelledby="service-publish">
          <h2 id="service-publish">{t("publishTitle")}</h2>
          <Notice result={draftResult} />
          <div className="services-actions">
            <Button variant="primary" busy={busy === "publish"} busyLabel={t("publishing")} onClick={() => void publish()}>
              {t("publish")}
            </Button>
            {s.status === "published" ? (
              <ConfirmAction label={t("retire")} question={t("retireQuestion")} confirmLabel={t("retireConfirm")} cancelLabel={t("keep")} onConfirm={retire} />
            ) : null}
          </div>
        </section>
      )}

      {preview ? (
        <section className="services-section" aria-labelledby="service-preview" data-testid="service-preview">
          <h2 id="service-preview">{t("previewTitle", { version: preview.version })}</h2>
          <p className="notice">{t("previewNote")}</p>
          {preview.approvalStep ? <p>{t("previewStep", { name: pick(preview.approvalStep, locale) })}</p> : null}
          <RequestForm target={{ kind: "preview" }} form={preview.form} initial={preview.prefill.values} readOnly={preview.prefill.readOnly} unconfirmed={preview.prefill.unconfirmed} units={units} />
        </section>
      ) : null}
    </div>
  );
}
