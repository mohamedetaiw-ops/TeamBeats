"use client";

import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { MAX_ACTION_STEPS, REQUEST_VALUES, type ActionStep, type Names, type RouteAction } from "@/lib/services/types";

type FieldRef = { key: string; label: string };

const keyOf = (a: { connectionId: string; action: string }) => `${a.connectionId}/${a.action}`;

/**
 * Automatic steps of a route (spec 016 US8, FR-016; spec 017 T009): after the approval, each step calls a connector
 * action with the request values the owner maps to its fields. Only mapped values are sent. A declared business error
 * can carry on or reject the request; any other failure stops at the step for an owner to retry, skip or cancel. The
 * API checks every step against the action when the service is published.
 */
export function ActionStepsEditor({
  steps,
  actions,
  fields,
  onChange,
}: {
  steps: ActionStep[];
  actions: RouteAction[];
  fields: FieldRef[];
  onChange: (steps: ActionStep[]) => void;
}) {
  const t = useTranslations("services.admin.automation");
  const sources = [
    { value: "", label: t("notMapped") },
    ...fields.map((f) => ({ value: `form.${f.key}`, label: t("formValue", { field: f.label }) })),
    ...REQUEST_VALUES.map((v) => ({ value: v, label: t(`values.${v.replace(".", "_")}`) })),
  ];
  const choices = [{ value: "", label: t("chooseAction") }, ...actions.map((a) => ({ value: keyOf(a), label: `${a.connectionName}: ${a.action}` }))];

  function update(i: number, next: ActionStep) {
    onChange(steps.map((s, n) => (n === i ? next : s)));
  }

  function choose(i: number, value: string) {
    const found = actions.find((a) => keyOf(a) === value);
    const names: Names | null = steps[i]?.names ?? null;
    update(i, found ? { names, connectionId: found.connectionId, action: found.action, inputs: {}, onBusinessError: null } : { names, connectionId: "", action: "", inputs: {} });
  }

  return (
    <fieldset>
      <legend>{t("title")}</legend>
      <p className="field-hint">{t("intro")}</p>
      {actions.length === 0 ? <p className="notice">{t("none")}</p> : null}
      <ol className="services-list" aria-label={t("title")}>
        {steps.map((s, i) => {
          const action = actions.find((a) => a.connectionId === s.connectionId && a.action === s.action);
          return (
            <li key={i} className="services-item" data-testid="action-step">
              <SelectField label={t("action")} name={`action-${i}`} value={s.connectionId ? keyOf(s) : ""} options={choices} onChange={(e) => choose(i, e.target.value)} />
              <div className="services-row">
                <FormField
                  label={t("nameEn")}
                  name={`action-${i}-en`}
                  dir="ltr"
                  lang="en"
                  maxLength={200}
                  value={s.names?.en ?? ""}
                  onChange={(e) => update(i, { ...s, names: { ...s.names, en: e.target.value } })}
                />
                <FormField
                  label={t("nameAr")}
                  name={`action-${i}-ar`}
                  dir="rtl"
                  lang="ar"
                  maxLength={200}
                  value={s.names?.ar ?? ""}
                  onChange={(e) => update(i, { ...s, names: { ...s.names, ar: e.target.value } })}
                />
              </div>
              {action ? (
                <>
                  <p className="field-hint">{action.sideEffect === "read" ? t("reads") : action.idempotent ? t("writesOnce") : t("writes")}</p>
                  {action.inputs.map((input) => (
                    <SelectField
                      key={input.name}
                      label={input.required ? t("inputRequired", { name: input.name }) : t("input", { name: input.name })}
                      name={`action-${i}-input-${input.name}`}
                      value={s.inputs[input.name] ?? ""}
                      options={sources}
                      onChange={(e) => {
                        const inputs = { ...s.inputs };
                        if (e.target.value === "") delete inputs[input.name];
                        else inputs[input.name] = e.target.value;
                        update(i, { ...s, inputs });
                      }}
                    />
                  ))}
                  {action.businessErrors.map((code) => (
                    <SelectField
                      key={code}
                      label={t("onError", { code })}
                      name={`action-${i}-error-${code}`}
                      value={s.onBusinessError?.[code] ?? ""}
                      options={[
                        { value: "", label: t("paths.stop") },
                        { value: "continue", label: t("paths.continue") },
                        { value: "reject", label: t("paths.reject") },
                      ]}
                      onChange={(e) => {
                        const paths = { ...(s.onBusinessError ?? {}) };
                        if (e.target.value === "") delete paths[code];
                        else paths[code] = e.target.value as "continue" | "reject";
                        update(i, { ...s, onBusinessError: Object.keys(paths).length > 0 ? paths : null });
                      }}
                    />
                  ))}
                </>
              ) : null}
              <Button variant="quiet" onClick={() => onChange(steps.filter((_, n) => n !== i))}>
                {t("remove")}
              </Button>
            </li>
          );
        })}
      </ol>
      {actions.length > 0 && steps.length < MAX_ACTION_STEPS ? (
        <Button onClick={() => onChange([...steps, { names: null, connectionId: "", action: "", inputs: {} }])}>{t("add")}</Button>
      ) : null}
    </fieldset>
  );
}
