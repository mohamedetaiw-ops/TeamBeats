"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import {
  JOURNEY_ASSIGNEES,
  JOURNEY_KINDS,
  JOURNEY_STARTS,
  JOURNEY_STEP_TYPES,
  type JourneyAssignee,
  type JourneyKind,
  type JourneyStart,
  type JourneyStepType,
  type JourneyTemplate,
} from "@/lib/journeys";
import "./journeys.css";

type ServiceOption = { id: string; names: Record<string, string> };

type StepText = {
  key: string;
  en: string;
  ar: string;
  offset: string;
  assignee: JourneyAssignee;
  users: string[];
  permission: string;
  due: string;
  type: JourneyStepType;
  systems: string;
  courses: string;
  contentEn: string;
  contentAr: string;
};

const blankStep = (): StepText => ({
  key: "",
  en: "",
  ar: "",
  offset: "0",
  assignee: "person",
  users: [],
  permission: "",
  due: "",
  type: "task",
  systems: "",
  courses: "",
  contentEn: "",
  contentAr: "",
});

/**
 * Journey templates (spec 022 FR-013): steps relative to an anchor date, each for the person, their buddy, their line
 * manager, named people or holders of a permission. The API validates every template, keeps versions and decides who
 * may change which; this form only collects the values.
 */
export function JourneyTemplates({ initial, units, services }: { initial: JourneyTemplate[]; units: OrgUnit[]; services: ServiceOption[] }) {
  const t = useTranslations("journeys.templates");
  const locale = useLocale();
  const [templates, setTemplates] = useState(initial);
  const [editing, setEditing] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function save(template: JourneyTemplate | null, body: string) {
    setMessages([]);
    setNotice(null);
    try {
      const response = template
        ? await fetch(`/api/v1/hr/journey-templates/${template.id}`, { method: "PUT", headers: { "content-type": "application/json", "If-Match": template.eTag }, body })
        : await fetch("/api/v1/hr/journey-templates", { method: "POST", headers: { "content-type": "application/json" }, body });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return false;
      }
      setNotice(t("saved"));
      setEditing(null);
      const list = await fetch("/api/v1/hr/journey-templates", { cache: "no-store" });
      if (list.ok) setTemplates((await list.json()) as JourneyTemplate[]);
      return true;
    } catch {
      setMessages([t("failed")]);
      return false;
    }
  }

  const unitLabel = (id: string | null) => {
    if (!id) return t("wholeOrganization");
    const unit = units.find((u) => u.id === id);
    return unit ? unitName(unit, locale) : id;
  };

  return (
    <section className="journeys-section" aria-labelledby="journey-templates">
      <h2 id="journey-templates">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {templates.length === 0 ? <p className="field-hint">{t("empty")}</p> : null}
      <ul className="journeys-list">
        {templates.map((x) => (
          <li key={x.id} className="journeys-item">
            <span>
              <bdi>{pick(x.names, locale, x.key)}</bdi> <Badge tone="info">{t(`kinds.${x.kind}`)}</Badge> <Badge>{t(`starts.${x.startOn}`)}</Badge>{" "}
              {x.active ? null : <Badge tone="warning">{t("inactive")}</Badge>}
              <span className="field-hint">
                {" "}
                {t("summary", { steps: x.steps.length, version: x.version })} · <bdi>{unitLabel(x.orgUnitId)}</bdi>
              </span>
            </span>
            <Button size="sm" onClick={() => setEditing(editing === x.id ? null : x.id)} aria-expanded={editing === x.id}>
              {editing === x.id ? t("close") : t("edit")}
            </Button>
            {editing === x.id ? <TemplateForm template={x} units={units} services={services} save={save} /> : null}
          </li>
        ))}
      </ul>
      <h3>{t("new")}</h3>
      <TemplateForm template={null} units={units} services={services} save={save} />
    </section>
  );
}

function TemplateForm({
  template,
  units,
  services,
  save,
}: {
  template: JourneyTemplate | null;
  units: OrgUnit[];
  services: ServiceOption[];
  save: (template: JourneyTemplate | null, body: string) => Promise<boolean>;
}) {
  const t = useTranslations("journeys.templates");
  const locale = useLocale();
  const prefix = template ? template.id.slice(0, 8) : "new";
  const [key, setKey] = useState(template?.key ?? "");
  const [kind, setKind] = useState<JourneyKind>(template?.kind ?? "onboarding");
  const [en, setEn] = useState(template?.names.en ?? "");
  const [ar, setAr] = useState(template?.names.ar ?? "");
  const [unit, setUnit] = useState(template?.orgUnitId ?? "");
  const [startOn, setStartOn] = useState<JourneyStart>(template?.startOn ?? "manual");
  const [serviceId, setServiceId] = useState(template?.serviceId ?? services[0]?.id ?? "");
  const [anchorField, setAnchorField] = useState(template?.anchorField ?? "");
  const [active, setActive] = useState(template?.active ?? true);
  const [busy, setBusy] = useState(false);
  const [steps, setSteps] = useState<StepText[]>(
    template
      ? template.steps.map((s) => ({
          key: s.key,
          en: s.names.en ?? "",
          ar: s.names.ar ?? "",
          offset: String(s.offsetDays),
          assignee: s.assignee,
          users: s.users ?? [],
          permission: s.permission ?? "",
          due: s.dueWorkingDays == null ? "" : String(s.dueWorkingDays),
          type: s.type ?? "task",
          systems: (s.systems ?? []).join("\n"),
          courses: (s.courses ?? []).join("\n"),
          contentEn: s.content?.en ?? "",
          contentAr: s.content?.ar ?? "",
        }))
      : [blankStep()],
  );

  const setStep = (i: number, change: Partial<StepText>) => setSteps((list) => list.map((s, j) => (j === i ? { ...s, ...change } : s)));
  const names = (a: string, b: string) => Object.fromEntries(Object.entries({ en: a.trim(), ar: b.trim() }).filter(([, v]) => v));

  const body = () =>
    JSON.stringify({
      key,
      kind,
      names: names(en, ar),
      orgUnitId: unit || null,
      startOn,
      serviceId: startOn === "service" ? serviceId || null : null,
      anchorField: startOn === "service" ? anchorField.trim() || null : null,
      active,
      steps: steps.map((s) => ({
        key: s.key.trim(),
        names: names(s.en, s.ar),
        offsetDays: Number(s.offset),
        assignee: s.assignee,
        users: s.assignee === "users" ? s.users.filter(Boolean) : null,
        permission: s.permission.trim() || null,
        dueWorkingDays: s.due.trim() ? Number(s.due) : null,
        type: s.type,
        systems:
          s.type === "access_removal"
            ? s.systems
                .split("\n")
                .map((n) => n.trim())
                .filter(Boolean)
            : null,
        content: s.type === "preboarding" ? names(s.contentEn, s.contentAr) : null,
        courses: s.type === "training" ? [...new Set(s.courses.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi) ?? [])] : null,
      })),
    });

  return (
    <form
      className="journeys-form"
      onSubmit={(e) => {
        e.preventDefault();
        setBusy(true);
        void save(template, body()).finally(() => setBusy(false));
      }}
    >
      <div className="ui-control-row">
        <FormField label={t("key")} hint={t("keyHint")} name={`${prefix}-key`} required readOnly={template !== null} value={key} onChange={(e) => setKey(e.target.value)} />
        <SelectField label={t("kind")} name={`${prefix}-kind`} value={kind} onChange={(e) => setKind(e.target.value as JourneyKind)} options={JOURNEY_KINDS.map((k) => ({ value: k, label: t(`kinds.${k}`) }))} />
        <SelectField
          label={t("startOn")}
          name={`${prefix}-start`}
          value={startOn}
          onChange={(e) => setStartOn(e.target.value as JourneyStart)}
          options={JOURNEY_STARTS.map((s) => ({ value: s, label: t(`starts.${s}`) }))}
        />
        {template ? null : (
          <SelectField
            label={t("unit")}
            name={`${prefix}-unit`}
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitName(u, locale) }))]}
          />
        )}
      </div>
      <div className="ui-control-row">
        <FormField label={t("nameEn")} name={`${prefix}-en`} maxLength={120} value={en} onChange={(e) => setEn(e.target.value)} />
        <FormField label={t("nameAr")} name={`${prefix}-ar`} dir="rtl" maxLength={120} value={ar} onChange={(e) => setAr(e.target.value)} />
      </div>
      {startOn === "service" ? (
        <div className="ui-control-row">
          <SelectField
            label={t("service")}
            name={`${prefix}-service`}
            value={serviceId}
            onChange={(e) => setServiceId(e.target.value)}
            options={services.map((s) => ({ value: s.id, label: pick(s.names, locale, s.id) }))}
          />
          <FormField label={t("anchorField")} hint={t("anchorFieldHint")} name={`${prefix}-anchor`} value={anchorField} onChange={(e) => setAnchorField(e.target.value)} />
        </div>
      ) : null}
      <p className="field-hint">{t(`startHints.${startOn}`)}</p>
      <h4>{t("steps")}</h4>
      {steps.map((s, i) => (
        <fieldset key={i} className="journeys-step">
          <legend>{t("stepNumber", { number: i + 1 })}</legend>
          <div className="ui-control-row">
            <FormField label={t("stepKey")} name={`${prefix}-s${i}-key`} required value={s.key} onChange={(e) => setStep(i, { key: e.target.value })} />
            <FormField label={t("nameEn")} name={`${prefix}-s${i}-en`} maxLength={120} value={s.en} onChange={(e) => setStep(i, { en: e.target.value })} />
            <FormField label={t("nameAr")} name={`${prefix}-s${i}-ar`} dir="rtl" maxLength={120} value={s.ar} onChange={(e) => setStep(i, { ar: e.target.value })} />
          </div>
          <div className="ui-control-row">
            <FormField
              label={t("offset")}
              hint={t("offsetHint")}
              name={`${prefix}-s${i}-offset`}
              type="number"
              min={-60}
              max={365}
              required
              value={s.offset}
              onChange={(e) => setStep(i, { offset: e.target.value })}
            />
            <SelectField
              label={t("type")}
              hint={t(`typeHints.${s.type}`)}
              name={`${prefix}-s${i}-type`}
              value={s.type}
              onChange={(e) => setStep(i, { type: e.target.value as JourneyStepType })}
              options={JOURNEY_STEP_TYPES.map((x) => ({ value: x, label: t(`types.${x}`) }))}
            />
            <SelectField
              label={t("assignee")}
              name={`${prefix}-s${i}-assignee`}
              value={s.assignee}
              onChange={(e) => setStep(i, { assignee: e.target.value as JourneyAssignee })}
              options={JOURNEY_ASSIGNEES.map((a) => ({ value: a, label: t(`assignees.${a}`) }))}
            />
            {s.assignee === "users" ? null : (
              <FormField
                label={s.assignee === "permission" ? t("permission") : t("fallback")}
                hint={s.assignee === "permission" ? t("permissionHint") : t("fallbackHint")}
                name={`${prefix}-s${i}-permission`}
                required={s.assignee === "permission"}
                value={s.permission}
                onChange={(e) => setStep(i, { permission: e.target.value })}
              />
            )}
            <FormField label={t("due")} hint={t("dueHint")} name={`${prefix}-s${i}-due`} type="number" min={1} max={60} value={s.due} onChange={(e) => setStep(i, { due: e.target.value })} />
          </div>
          {s.type === "access_removal" ? (
            <TextAreaField
              label={t("systems")}
              hint={t("systemsHint")}
              name={`${prefix}-s${i}-systems`}
              rows={3}
              value={s.systems}
              onChange={(e) => setStep(i, { systems: e.target.value })}
            />
          ) : null}
          {s.type === "training" ? (
            <TextAreaField
              label={t("courses")}
              hint={t("coursesHint")}
              name={`${prefix}-s${i}-courses`}
              rows={2}
              value={s.courses}
              onChange={(e) => setStep(i, { courses: e.target.value })}
            />
          ) : null}
          {s.type === "preboarding" ? (
            <div className="ui-control-row">
              <TextAreaField
                label={t("contentEn")}
                hint={t("contentHint")}
                name={`${prefix}-s${i}-content-en`}
                rows={4}
                maxLength={2000}
                value={s.contentEn}
                onChange={(e) => setStep(i, { contentEn: e.target.value })}
              />
              <TextAreaField
                label={t("contentAr")}
                name={`${prefix}-s${i}-content-ar`}
                dir="rtl"
                rows={4}
                maxLength={2000}
                value={s.contentAr}
                onChange={(e) => setStep(i, { contentAr: e.target.value })}
              />
            </div>
          ) : null}
          {s.assignee === "users"
            ? [...s.users, ""].map((u, j) => (
                <PersonPicker
                  key={`${j}-${u}`}
                  label={t("person", { number: j + 1 })}
                  value={u || null}
                  onChange={(id) => setStep(i, { users: [...s.users.slice(0, j), ...(id ? [id] : []), ...s.users.slice(j + 1)] })}
                />
              ))
            : null}
          <div className="ui-control-row">
            <Button size="sm" variant="quiet" disabled={steps.length === 1} onClick={() => setSteps((list) => list.filter((_, j) => j !== i))}>
              {t("removeStep")}
            </Button>
          </div>
        </fieldset>
      ))}
      <div className="ui-control-row">
        <Button size="sm" onClick={() => setSteps((list) => [...list, blankStep()])} disabled={steps.length >= 50}>
          {t("addStep")}
        </Button>
      </div>
      <Checkbox label={t("active")} name={`${prefix}-active`} checked={active} onChange={(e) => setActive(e.target.checked)} />
      <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
        {template ? t("save") : t("create")}
      </Button>
    </form>
  );
}
