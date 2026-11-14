"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { IconButton } from "@/components/ui/IconButton";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { problemMessages } from "@/lib/notifications/types";
import {
  END_RESULTS,
  FREQUENCIES,
  STEP_OUTCOMES,
  TRIGGER_EVENTS,
  isHuman,
  type RouteGraph,
  type RouteTestResult,
  type RouteTestStep,
  type ServiceTrigger,
} from "@/lib/services/route-graph";
import { pick } from "@/lib/services/types";
import type { DesignerField, DesignerService } from "./RouteDesigner";
import { PersonPicker } from "./PersonPicker";

/**
 * Route settings that apply to every request of the version (spec 016 FR-014, FR-015): the service level target,
 * whether open requests go on after the requester leaves, and the start triggers. The API checks each one.
 */
export function RouteSettings({
  serviceLevelDays,
  continueAfterExit,
  triggers,
  services,
  units,
  onChange,
}: {
  serviceLevelDays: number | null;
  continueAfterExit: boolean;
  triggers: ServiceTrigger[];
  services: DesignerService[];
  units: OrgUnit[];
  onChange: (patch: { serviceLevelDays?: number | null; continueAfterExit?: boolean; triggers?: ServiceTrigger[] }) => void;
}) {
  const t = useTranslations("services.designer");
  const locale = useLocale();
  const set = (i: number, patch: Partial<ServiceTrigger>) =>
    onChange({
      triggers: triggers.map((x, j) => (j === i ? { ...x, ...patch } : x)),
    });
  return (
    <fieldset className="route-conditions" data-testid="route-settings">
      <legend>{t("settingsTitle")}</legend>
      <FormField
        label={t("serviceLevel")}
        hint={t("serviceLevelHint")}
        name="service-level"
        type="number"
        min={1}
        max={90}
        value={serviceLevelDays ?? ""}
        onChange={(e) =>
          onChange({
            serviceLevelDays: e.target.value === "" ? null : Number(e.target.value),
          })
        }
      />
      <Checkbox
        label={t("continueAfterExit")}
        hint={t("continueAfterExitHint")}
        checked={continueAfterExit}
        onChange={(e) => onChange({ continueAfterExit: e.target.checked })}
      />
      <h3>{t("triggers")}</h3>
      <p className="field-hint">{t("triggersHint")}</p>
      {triggers.map((tr, i) => (
        <fieldset key={i} className="route-conditions">
          <legend>{t("triggerTitle", { position: i + 1 })}</legend>
          <SelectField
            label={t("triggerKind")}
            name={`trigger-kind-${i}`}
            value={tr.kind}
            onChange={(e) =>
              set(
                i,
                e.target.value === "schedule"
                  ? {
                      kind: "schedule",
                      event: null,
                      frequency: "monthly",
                      day: 1,
                    }
                  : {
                      kind: "event",
                      event: "user_provisioned",
                      frequency: null,
                      day: null,
                    },
              )
            }
            options={[
              { value: "schedule", label: t("triggerKinds.schedule") },
              { value: "event", label: t("triggerKinds.event") },
            ]}
          />
          {tr.kind === "schedule" ? (
            <div className="services-row">
              <SelectField
                label={t("frequency")}
                name={`trigger-frequency-${i}`}
                value={tr.frequency ?? "monthly"}
                onChange={(e) => set(i, { frequency: e.target.value })}
                options={FREQUENCIES.map((f) => ({
                  value: f,
                  label: t(`frequencies.${f}`),
                }))}
              />
              <FormField
                label={t("day")}
                hint={tr.frequency === "weekly" ? t("dayWeeklyHint") : t("dayMonthlyHint")}
                name={`trigger-day-${i}`}
                type="number"
                min={1}
                max={tr.frequency === "weekly" ? 7 : 28}
                value={tr.day ?? ""}
                onChange={(e) =>
                  set(i, {
                    day: e.target.value === "" ? null : Number(e.target.value),
                  })
                }
              />
            </div>
          ) : (
            <>
              <SelectField
                label={t("event")}
                name={`trigger-event-${i}`}
                value={tr.event ?? ""}
                onChange={(e) =>
                  set(i, {
                    event: e.target.value,
                    orgUnitId: null,
                    serviceId: null,
                    status: null,
                  })
                }
                options={TRIGGER_EVENTS.map((ev) => ({
                  value: ev,
                  label: t(`events.${ev}`),
                }))}
              />
              {tr.event === "membership_changed" ? (
                <SelectField
                  label={t("triggerUnit")}
                  name={`trigger-unit-${i}`}
                  value={tr.orgUnitId ?? ""}
                  onChange={(e) => set(i, { orgUnitId: e.target.value || null })}
                  options={[
                    { value: "", label: t("anyUnit") },
                    ...units.map((u) => ({
                      value: u.id,
                      label: unitName(u, locale),
                    })),
                  ]}
                />
              ) : null}
              {tr.event === "request_completed" ? (
                <div className="services-row">
                  <SelectField
                    label={t("triggerService")}
                    name={`trigger-service-${i}`}
                    value={tr.serviceId ?? ""}
                    onChange={(e) => set(i, { serviceId: e.target.value || null })}
                    options={[{ value: "", label: t("chooseService") }, ...services.map((s) => ({ value: s.id, label: s.name }))]}
                  />
                  <SelectField
                    label={t("triggerStatus")}
                    name={`trigger-status-${i}`}
                    value={tr.status ?? ""}
                    onChange={(e) => set(i, { status: e.target.value || null })}
                    options={[
                      { value: "", label: t("anyStatus") },
                      ...END_RESULTS.map((r) => ({
                        value: r,
                        label: t(`results.${r}`),
                      })),
                    ]}
                  />
                </div>
              ) : null}
            </>
          )}
          <PersonPicker
            label={tr.kind === "schedule" ? t("triggerRequester") : t("triggerFiler")}
            value={tr.requesterId ?? null}
            onChange={(id) => set(i, { requesterId: id })}
          />
          <div className="services-actions">
            <IconButton
              icon="trash"
              label={t("removeTrigger", { position: i + 1 })}
              onClick={() => onChange({ triggers: triggers.filter((_, j) => j !== i) })}
            />
          </div>
        </fieldset>
      ))}
      <div>
        <Button
          variant="secondary"
          size="sm"
          disabled={triggers.length >= 10}
          onClick={() =>
            onChange({
              triggers: [...triggers, { kind: "event", event: "user_provisioned" }],
            })
          }
        >
          {t("addTrigger")}
        </Button>
      </div>
    </fieldset>
  );
}

/**
 * Test mode (spec 016 US8 scenario 6): the saved draft runs in the sandbox as a sample employee with sample form values
 * and the outcome picked per person step. Nothing is created or sent. The steps reached are shown here and on the
 * canvas, with who each person step would go to.
 */
export function RouteTestPanel({
  serviceId,
  graph,
  fields,
  save,
  onResult,
}: {
  serviceId: string;
  graph: RouteGraph;
  fields: DesignerField[];
  save: () => Promise<boolean>;
  onResult: (result: RouteTestResult | null) => void;
}) {
  const t = useTranslations("services.designer");
  const locale = useLocale();
  const format = useFormatter();
  const [requester, setRequester] = useState<string | null>(null);
  const [data, setData] = useState<Record<string, string>>({});
  const [outcomes, setOutcomes] = useState<Record<string, string>>({});
  const [run, setRun] = useState<RouteTestResult | null>(null);
  const [error, setError] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);

  const nameOf = (id: string) => {
    const s = graph.steps.find((x) => x.id === id);
    return s ? pick(s.names, locale, `${t(`types.${s.type}`)} (${s.id})`) : id;
  };
  const people = graph.steps.filter((s) => isHuman(s.type));

  async function start() {
    setBusy(true);
    setError(null);
    try {
      if (!(await save())) return;
      const values = Object.fromEntries(
        Object.entries(data)
          .filter(([, v]) => v !== "")
          .map(([k, v]) => [k, fields.find((f) => f.key === k)?.numeric ? Number(v) : v]),
      );
      const response = await fetch(`/api/v1/services/admin/${serviceId}/test`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          requesterId: requester,
          data: values,
          outcomes,
        }),
      });
      if (!response.ok) {
        setError(await problemMessages(response));
        setRun(null);
        onResult(null);
        return;
      }
      const result = (await response.json()) as RouteTestResult;
      setRun(result);
      onResult(result);
    } finally {
      setBusy(false);
    }
  }

  function stepText(step: RouteTestStep): string | null {
    const [kind, arg = ""] = step.event.split(/:(.*)/s);
    switch (kind) {
      case "took": {
        const link = /^link_(\d+)$/.exec(arg);
        const to = link ? graph.links[Number(link[1])]?.to : undefined;
        return to ? t("testEvents.took", { to: nameOf(to) }) : null;
      }
      case "assigned": {
        const who = [
          ...(step.assignees ?? []).map((a) => a.name),
          ...(step.lineManager && !(step.assignees ?? []).length ? [t("testLineManager")] : []),
          ...(step.permission ? [t("testPermission", { permission: step.permission })] : []),
        ];
        return who.length > 0
          ? t("testEvents.assigned", {
              who: format.list(who, { type: "disjunction" }),
            })
          : t("testEvents.assignedNobody");
      }
      case "auto_approved":
      case "completed":
      case "fast_forwarded":
      case "mocked":
      case "action_not_run":
      case "ended":
      case "joined":
      case "waiting":
      case "incident":
      case "no_assignee":
        return t(`testEvents.${kind}`);
      default:
        return t("testEvents.reached");
    }
  }

  const [outcomeKind, outcomeResult] = (run?.outcome ?? "").split(":");
  const lines = (run?.steps ?? []).map((s) => ({ step: s, text: stepText(s) })).filter((l) => l.text !== null);
  return (
    <section className="services-section" aria-labelledby="route-test" data-testid="route-test">
      <h2 id="route-test">{t("testTitle")}</h2>
      <p className="field-hint">{t("testIntro")}</p>
      <PersonPicker label={t("testRequester")} value={requester} onChange={setRequester} />
      {fields.length > 0 ? (
        <fieldset className="route-conditions">
          <legend>{t("testData")}</legend>
          <div className="services-row">
            {fields.map((f) => (
              <FormField
                key={f.key}
                label={f.label}
                name={`test-${f.key}`}
                type={f.numeric ? "number" : "text"}
                value={data[f.key] ?? ""}
                onChange={(e) => setData((d) => ({ ...d, [f.key]: e.target.value }))}
              />
            ))}
          </div>
        </fieldset>
      ) : null}
      {people.length > 0 ? (
        <div className="services-row">
          {people.map((s) => (
            <SelectField
              key={s.id}
              label={t("testOutcome", { name: nameOf(s.id) })}
              name={`test-outcome-${s.id}`}
              value={outcomes[s.id] ?? "approve"}
              onChange={(e) => setOutcomes((o) => ({ ...o, [s.id]: e.target.value }))}
              options={STEP_OUTCOMES.filter((o) => s.type === "approval" || o !== "return").map((o) => ({ value: o, label: t(`testOutcomes.${o}`) }))}
            />
          ))}
        </div>
      ) : null}
      <div className="services-actions">
        <Button variant="secondary" busy={busy} busyLabel={t("testRunning")} onClick={() => void start()}>
          {t("runTest")}
        </Button>
      </div>
      {error ? (
        <div className="notice notice-error" role="alert">
          {error.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {run ? (
        <div className="notice" role="status" data-testid="route-test-result">
          <h3>
            {outcomeResult && (outcomeKind === "completed" || outcomeKind === "terminated")
              ? t("testEndedAs", {
                  result: t.has(`results.${outcomeResult}`) ? t(`results.${outcomeResult}`) : outcomeResult,
                })
              : t.has(`testOutcomeTitles.${outcomeKind}`)
                ? t(`testOutcomeTitles.${outcomeKind}`)
                : t("testOutcomeTitles.other")}
          </h3>
          {run.problems.length > 0 ? (
            <ul className="services-list field-error">
              {run.problems.map((p, i) => (
                <li key={i}>
                  {t("problemOn", {
                    name: nameOf(p.stepId),
                    problem: p.message || p.key,
                  })}
                </li>
              ))}
            </ul>
          ) : (
            <ol className="services-list" aria-label={t("testSteps")}>
              {lines.map(({ step, text }, i) => (
                <li key={i}>
                  <bdi>{nameOf(step.stepId)}</bdi>
                  {t("testStepSeparator")}
                  {text}
                </li>
              ))}
            </ol>
          )}
        </div>
      ) : null}
    </section>
  );
}
