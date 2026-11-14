"use client";

import { useMemo, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Controls, Handle, MarkerType, Position, ReactFlow, type Connection, type Edge, type Node, type NodeChange, type NodeProps } from "@xyflow/react";
import "@xyflow/react/dist/base.css";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { Icon, type IconName } from "@/components/ui/Icon";
import { IconButton } from "@/components/ui/IconButton";
import {
  ASSIGNEE_KINDS,
  END_RESULTS,
  OPERATORS,
  STEP_TYPES,
  blankStep,
  branches,
  isHuman,
  layout,
  newStepId,
  firstStep,
  readingOrder,
  type AssigneeKind,
  type Operator,
  type RouteGraph,
  type RouteGroup,
  type RouteLink,
  type RouteNode,
  type StepCondition,
  type StepType,
} from "@/lib/services/route-graph";
import { pick, REQUEST_VALUES, type Names, type RouteAction } from "@/lib/services/types";
import { PersonPicker } from "./PersonPicker";
import "./services.css";

export type DesignerField = { key: string; label: string; numeric: boolean };
export type DesignerService = { id: string; name: string };

const STEP_ICONS: Record<StepType, IconName> = {
  approval: "check",
  task: "user",
  condition: "filter",
  split: "plus",
  join: "link",
  set_field: "edit",
  notify: "bell",
  connector: "settings",
  webhook: "arrow-forward",
  wait: "clock",
  start_service: "services",
  end: "stop",
};

type StepData = {
  label: string;
  type: StepType;
  invalid: boolean;
  visited: boolean;
};
type StepNode = Node<StepData, "step">;

/** One step on the canvas: a rounded box with the step type's icon, marked when it has problems or a test run reached it. */
function StepShape({ data, selected }: NodeProps<StepNode>) {
  const shape = data.type === "condition" || data.type === "split" || data.type === "join" ? "gateway" : data.type === "end" ? "event" : "task";
  return (
    <div
      className="route-node"
      data-shape={shape}
      data-type={data.type}
      data-invalid={data.invalid || undefined}
      data-visited={data.visited || undefined}
      data-selected={selected || undefined}
    >
      <Handle type="target" position={Position.Left} />
      <span className="route-node-mark" aria-hidden="true">
        <Icon name={STEP_ICONS[data.type]} size={14} mirrorInRtl={false} />
      </span>
      <bdi className="route-node-label">{data.label}</bdi>
      {data.invalid ? (
        <span className="route-node-problem" aria-hidden="true">
          <Icon name="alert" size={14} />
        </span>
      ) : null}
      <Handle type="source" position={Position.Right} />
    </div>
  );
}
const nodeTypes = { step: StepShape };

/** The comparisons of an auto-approve rule or a path: each a form field against a constant, all of which must hold. */
function ConditionsEditor({
  title,
  conditions,
  fields,
  onChange,
}: {
  title: string;
  conditions: StepCondition[];
  fields: DesignerField[];
  onChange: (c: StepCondition[]) => void;
}) {
  const t = useTranslations("services.designer");
  const numeric = (key: string) => fields.find((f) => f.key === key)?.numeric ?? false;
  const set = (i: number, patch: Partial<StepCondition>) => onChange(conditions.map((c, j) => (j === i ? { ...c, ...patch } : c)));
  return (
    <fieldset className="route-conditions">
      <legend>{title}</legend>
      {conditions.length === 0 ? <p className="field-hint">{t("noConditions")}</p> : null}
      {conditions.map((c, i) => (
        <div key={i} className="services-row">
          <SelectField
            label={t("conditionField", { position: i + 1 })}
            name={`${title}-field-${i}`}
            value={c.field}
            onChange={(e) => set(i, { field: e.target.value })}
            options={[{ value: "", label: t("chooseField") }, ...fields.map((f) => ({ value: f.key, label: f.label }))]}
          />
          <SelectField
            label={t("conditionOperator", { position: i + 1 })}
            name={`${title}-op-${i}`}
            value={c.operator}
            onChange={(e) => set(i, { operator: e.target.value as Operator })}
            options={OPERATORS.map((o) => ({
              value: o,
              label: t(`operators.${o}`),
            }))}
          />
          <FormField
            label={t("conditionValue", { position: i + 1 })}
            name={`${title}-value-${i}`}
            dir="ltr"
            type={numeric(c.field) ? "number" : "text"}
            value={String(c.value)}
            onChange={(e) =>
              set(i, {
                value: numeric(c.field) && e.target.value !== "" ? Number(e.target.value) : e.target.value,
              })
            }
          />
          <IconButton icon="trash" label={t("removeCondition", { position: i + 1 })} onClick={() => onChange(conditions.filter((_, j) => j !== i))} />
        </div>
      ))}
      <div>
        <Button
          variant="secondary"
          size="sm"
          disabled={fields.length === 0 || conditions.length >= 10}
          onClick={() => onChange([...conditions, { field: fields[0]?.key ?? "", operator: "eq", value: "" }])}
        >
          {t("addCondition")}
        </Button>
      </div>
    </fieldset>
  );
}

/** A list of chosen people with a picker to add one more. */
function PeopleList({ label, ids, onChange }: { label: string; ids: string[]; onChange: (ids: string[]) => void }) {
  const t = useTranslations("services.designer");
  return (
    <fieldset className="route-conditions">
      <legend>{label}</legend>
      <ul className="services-list">
        {ids.map((id) => (
          <li key={id} className="services-row">
            <PersonPicker label={t("person")} value={id} readOnly onChange={() => undefined} />
            <Button variant="quiet" size="sm" onClick={() => onChange(ids.filter((x) => x !== id))}>
              {t("removePerson")}
            </Button>
          </li>
        ))}
      </ul>
      <PersonPicker label={t("addPerson")} value={null} onChange={(id) => id && onChange([...new Set([...ids, id])])} />
    </fieldset>
  );
}

/** Group check boxes, or a note when the organization has no groups yet. */
function GroupChoice({ label, groups, ids, onChange }: { label: string; groups: RouteGroup[]; ids: string[]; onChange: (ids: string[]) => void }) {
  const t = useTranslations("services.designer");
  return (
    <fieldset className="route-conditions">
      <legend>{label}</legend>
      {groups.length === 0 ? <p className="field-hint">{t("noGroups")}</p> : null}
      {groups.map((g) => (
        <Checkbox
          key={g.id}
          label={t("groupLine", { name: g.name, count: g.members })}
          checked={ids.includes(g.id)}
          onChange={(e) => onChange(e.target.checked ? [...ids, g.id] : ids.filter((x) => x !== g.id))}
        />
      ))}
    </fieldset>
  );
}

/**
 * The route designer of spec 016 US8 on React Flow, like the process modeler (ADR-0017): a palette of service steps,
 * the canvas, a properties panel per step with its paths, and a text outline that lists the route in reading order.
 * Every action works from the keyboard: the palette adds a step after the selected one, paths are added from the
 * properties panel, and Delete removes the selected step. Problems the server found are shown on their steps; the
 * server decides what a valid route is.
 */
export function RouteDesigner({
  graph,
  onChange,
  fields,
  groups,
  services,
  actions,
  problems,
  visited,
}: {
  graph: RouteGraph;
  onChange: (g: RouteGraph) => void;
  fields: DesignerField[];
  groups: RouteGroup[];
  services: DesignerService[];
  actions: RouteAction[];
  problems: Map<string, string[]>;
  visited: Set<string>;
}) {
  const t = useTranslations("services.designer");
  const locale = useLocale();
  const format = useFormatter();
  const [selected, setSelected] = useState<string | null>(null);
  const [moved, setMoved] = useState<Map<string, { x: number; y: number }>>(new Map());
  const [notice, setNotice] = useState<string | null>(null);

  const byId = useMemo(() => new Map(graph.steps.map((s) => [s.id, s])), [graph.steps]);
  const typeName = (type: StepType) => t(`types.${type}`);
  const nameOf = (id: string) => {
    const s = byId.get(id);
    return s ? pick(s.names, locale, `${typeName(s.type)} (${s.id})`) : id;
  };
  const current = selected ? byId.get(selected) : undefined;
  const positions = useMemo(() => layout(graph), [graph]);
  const order = useMemo(() => readingOrder(graph), [graph]);

  const updateStep = (id: string, patch: Partial<RouteNode>) =>
    onChange({
      ...graph,
      steps: graph.steps.map((s) => (s.id === id ? { ...s, ...patch } : s)),
    });
  const setLinks = (links: RouteLink[]) => onChange({ ...graph, links });

  function connect(from: string, to: string) {
    if (!from || !to || from === to || graph.links.some((l) => l.from === from && l.to === to)) return;
    setLinks([...graph.links, { from, to }]);
    setNotice(t("connected", { from: nameOf(from), to: nameOf(to) }));
  }

  /** Adds a step; after a selected step with one path, the new step goes between it and where it went. */
  function add(type: StepType) {
    const step = blankStep(type, newStepId(type, graph));
    const anchor = current;
    let links = graph.links;
    if (anchor && anchor.type !== "end") {
      const out = links.filter((l) => l.from === anchor.id);
      if (out.length === 1 && !branches(anchor.type) && type !== "end") {
        links = [...links.filter((l) => l !== out[0]), { from: anchor.id, to: step.id }, { from: step.id, to: out[0]!.to }];
      } else {
        links = [...links, { from: anchor.id, to: step.id }];
      }
    } else if (!anchor && type !== "end") {
      // With nothing selected the new step goes at the start, before the current first step.
      const first = firstStep(graph);
      if (first) links = [...links, { from: step.id, to: first.id }];
    }
    onChange({ steps: [...graph.steps, step], links });
    setSelected(step.id);
    setNotice(t("added", { type: typeName(type) }));
  }

  /** Removes a step; a step with one way in and one way out is bridged so the route stays joined up. */
  function remove(id: string) {
    const into = graph.links.filter((l) => l.to === id);
    const out = graph.links.filter((l) => l.from === id);
    let links = graph.links.filter((l) => l.from !== id && l.to !== id);
    if (into.length === 1 && out.length === 1 && !links.some((l) => l.from === into[0]!.from && l.to === out[0]!.to)) {
      links = [...links, { from: into[0]!.from, to: out[0]!.to, when: into[0]!.when }];
    }
    onChange({ steps: graph.steps.filter((s) => s.id !== id), links });
    setNotice(t("removed", { name: nameOf(id) }));
    setSelected(null);
  }

  const nodes: StepNode[] = graph.steps.map((s) => ({
    id: s.id,
    type: "step",
    position: moved.get(s.id) ?? positions.get(s.id) ?? { x: 0, y: 0 },
    width: 150,
    height: 60,
    selected: selected === s.id,
    ariaLabel: t("typeAndName", { type: typeName(s.type), name: nameOf(s.id) }),
    data: {
      label: nameOf(s.id),
      type: s.type,
      invalid: (problems.get(s.id) ?? []).length > 0,
      visited: visited.has(s.id),
    },
  }));
  const edges: Edge[] = graph.links.map((l) => ({
    id: `${l.from}->${l.to}`,
    source: l.from,
    target: l.to,
    label: l.when && l.when.length > 0 ? t("conditional") : branches(byId.get(l.from)?.type ?? "end") ? t("otherwise") : undefined,
    markerEnd: {
      type: MarkerType.ArrowClosed,
      color: "var(--tb-color-text-secondary)",
    },
    ariaLabel: t("pathFromTo", { from: nameOf(l.from), to: nameOf(l.to) }),
  }));

  function onNodesChange(changes: NodeChange<StepNode>[]) {
    for (const c of changes) {
      if (c.type === "position" && c.position) {
        const p = { x: Math.round(c.position.x), y: Math.round(c.position.y) };
        setMoved((m) => new Map(m).set(c.id, p));
      } else if (c.type === "select") {
        setSelected((s) => (c.selected ? c.id : s === c.id ? null : s));
      } else if (c.type === "remove") {
        remove(c.id);
      }
    }
  }

  function assigneeEditor(step: RouteNode) {
    const a = step.assignee ?? { kind: "line_manager" as AssigneeKind };
    const set = (patch: Partial<typeof a>) => updateStep(step.id, { assignee: { ...a, ...patch } });
    return (
      <>
        <SelectField
          label={t("assignee")}
          name="assignee-kind"
          value={a.kind}
          onChange={(e) => {
            const kind = e.target.value as AssigneeKind;
            updateStep(step.id, {
              assignee:
                kind === "permission_at_unit"
                  ? { kind, permission: "workflow.approve" }
                  : kind === "manager_level"
                    ? { kind, level: 2 }
                    : {
                        kind,
                        users: kind === "users" ? [] : null,
                        groups: kind === "group" ? [] : null,
                      },
            });
          }}
          options={ASSIGNEE_KINDS.map((k) => ({
            value: k,
            label: t(`assignees.${k}`),
          }))}
        />
        {a.kind === "users" ? <PeopleList label={t("assigneeUsers")} ids={a.users ?? []} onChange={(users) => set({ users })} /> : null}
        {a.kind === "permission_at_unit" ? (
          <FormField
            label={t("permission")}
            hint={t("permissionHint")}
            name="assignee-permission"
            dir="ltr"
            value={a.permission ?? ""}
            onChange={(e) => set({ permission: e.target.value })}
          />
        ) : null}
        {a.kind === "manager_level" ? (
          <FormField
            label={t("managerLevel")}
            hint={t("managerLevelHint")}
            name="assignee-level"
            type="number"
            min={1}
            max={5}
            value={a.level ?? ""}
            onChange={(e) =>
              set({
                level: e.target.value === "" ? null : Number(e.target.value),
              })
            }
          />
        ) : null}
        {a.kind === "line_manager" ? <p className="field-hint">{t("lineManagerHint")}</p> : null}
        {a.kind === "group" ? <GroupChoice label={t("assigneeGroups")} groups={groups} ids={a.groups ?? []} onChange={(ids) => set({ groups: ids })} /> : null}
        <FormField
          label={t("dueDays")}
          hint={t("dueDaysHint")}
          name="due-days"
          type="number"
          min={1}
          max={90}
          value={step.dueWorkingDays ?? ""}
          onChange={(e) =>
            updateStep(step.id, {
              dueWorkingDays: e.target.value === "" ? null : Number(e.target.value),
              escalation: e.target.value === "" ? null : step.escalation,
            })
          }
        />
        {step.dueWorkingDays ? (
          <Checkbox
            label={t("escalate")}
            checked={!!step.escalation}
            onChange={(e) =>
              updateStep(step.id, {
                escalation: e.target.checked ? { afterWorkingDays: 1, users: [], groups: [] } : null,
              })
            }
          />
        ) : null}
        {step.escalation ? (
          <>
            <FormField
              label={t("escalateAfter")}
              name="escalate-after"
              type="number"
              min={1}
              max={90}
              value={step.escalation.afterWorkingDays}
              onChange={(e) =>
                updateStep(step.id, {
                  escalation: {
                    ...step.escalation!,
                    afterWorkingDays: Number(e.target.value || 1),
                  },
                })
              }
            />
            <PeopleList
              label={t("escalateUsers")}
              ids={step.escalation.users ?? []}
              onChange={(users) =>
                updateStep(step.id, {
                  escalation: { ...step.escalation!, users },
                })
              }
            />
            <GroupChoice
              label={t("escalateGroups")}
              groups={groups}
              ids={step.escalation.groups ?? []}
              onChange={(ids) =>
                updateStep(step.id, {
                  escalation: { ...step.escalation!, groups: ids },
                })
              }
            />
          </>
        ) : null}
        {step.type === "approval" ? (
          <ConditionsEditor
            title={t("autoApprove")}
            conditions={step.autoApprove ?? []}
            fields={fields}
            onChange={(autoApprove) => updateStep(step.id, { autoApprove })}
          />
        ) : null}
        {step.type === "approval" ? <p className="field-hint">{t("autoApproveHint")}</p> : null}
      </>
    );
  }

  function connectorEditor(step: RouteNode) {
    const key = step.connectionId && step.action ? `${step.connectionId}|${step.action}` : "";
    const action = actions.find((a) => `${a.connectionId}|${a.action}` === key);
    const sources = [
      ...fields.map((f) => ({ value: `form.${f.key}`, label: f.label })),
      ...REQUEST_VALUES.map((v) => ({
        value: v,
        label: t(`values.${v.replace(".", "_")}`),
      })),
    ];
    return (
      <>
        <SelectField
          label={t("action")}
          name="connector-action"
          value={key}
          onChange={(e) => {
            const [connectionId, name] = e.target.value.split("|");
            updateStep(step.id, {
              connectionId: connectionId || null,
              action: name || null,
              inputs: {},
            });
          }}
          options={[
            { value: "", label: t("chooseAction") },
            ...actions.map((a) => ({
              value: `${a.connectionId}|${a.action}`,
              label: `${a.connectionName}: ${a.action}`,
            })),
          ]}
        />
        {actions.length === 0 ? <p className="field-hint">{t("noActions")}</p> : null}
        {action?.inputs.map((input) => (
          <SelectField
            key={input.name}
            label={input.required ? t("inputRequired", { name: input.name }) : t("input", { name: input.name })}
            name={`input-${input.name}`}
            value={step.inputs?.[input.name] ?? ""}
            onChange={(e) => {
              const inputs = { ...(step.inputs ?? {}) };
              if (e.target.value) inputs[input.name] = e.target.value;
              else delete inputs[input.name];
              updateStep(step.id, { inputs });
            }}
            options={[{ value: "", label: t("noValue") }, ...sources]}
          />
        ))}
      </>
    );
  }

  function settings(step: RouteNode) {
    switch (step.type) {
      case "approval":
      case "task":
        return assigneeEditor(step);
      case "connector":
        return connectorEditor(step);
      case "webhook":
        return (
          <FormField
            label={t("webhook")}
            hint={t("webhookHint")}
            name="webhook-id"
            dir="ltr"
            value={step.webhookId ?? ""}
            onChange={(e) => updateStep(step.id, { webhookId: e.target.value || null })}
          />
        );
      case "set_field":
        return (
          <>
            <FormField
              label={t("setField")}
              hint={t("setFieldHint")}
              name="set-field"
              dir="ltr"
              value={step.field ?? ""}
              onChange={(e) => updateStep(step.id, { field: e.target.value })}
            />
            <FormField label={t("setValue")} name="set-value" value={step.value ?? ""} onChange={(e) => updateStep(step.id, { value: e.target.value })} />
          </>
        );
      case "notify": {
        const n = step.notify ?? { requester: false, subject: false };
        const set = (patch: Partial<typeof n>) => updateStep(step.id, { notify: { ...n, ...patch } });
        return (
          <>
            <Checkbox label={t("notifyRequester")} checked={n.requester} onChange={(e) => set({ requester: e.target.checked })} />
            <Checkbox label={t("notifySubject")} checked={n.subject} onChange={(e) => set({ subject: e.target.checked })} />
            <PeopleList label={t("notifyUsers")} ids={n.users ?? []} onChange={(users) => set({ users })} />
            <GroupChoice label={t("notifyGroups")} groups={groups} ids={n.groups ?? []} onChange={(ids) => set({ groups: ids })} />
          </>
        );
      }
      case "wait":
        return (
          <FormField
            label={t("waitDays")}
            name="wait-days"
            type="number"
            min={1}
            max={365}
            value={step.waitWorkingDays ?? ""}
            onChange={(e) =>
              updateStep(step.id, {
                waitWorkingDays: e.target.value === "" ? null : Number(e.target.value),
              })
            }
          />
        );
      case "start_service":
        return (
          <SelectField
            label={t("startService")}
            hint={t("startServiceHint")}
            name="start-service"
            value={step.serviceId ?? ""}
            onChange={(e) => updateStep(step.id, { serviceId: e.target.value || null })}
            options={[{ value: "", label: t("chooseService") }, ...services.map((s) => ({ value: s.id, label: s.name }))]}
          />
        );
      case "end":
        return (
          <SelectField
            label={t("endResult")}
            name="end-result"
            value={step.result ?? "completed"}
            onChange={(e) => updateStep(step.id, { result: e.target.value })}
            options={END_RESULTS.map((r) => ({
              value: r,
              label: t(`results.${r}`),
            }))}
          />
        );
      case "condition":
        return <p className="field-hint">{t("conditionHint")}</p>;
      case "split":
        return <p className="field-hint">{t("splitHint")}</p>;
      case "join":
        return <p className="field-hint">{t("joinHint")}</p>;
    }
  }

  function stepPanel(step: RouteNode) {
    const out = graph.links.filter((l) => l.from === step.id);
    const targets = graph.steps.filter((s) => s.id !== step.id && !out.some((l) => l.to === s.id));
    const errors = problems.get(step.id) ?? [];
    return (
      <>
        <h3 id="designer-properties-title">
          {t("typeAndName", {
            type: typeName(step.type),
            name: nameOf(step.id),
          })}
        </h3>
        {errors.length > 0 ? (
          <ul className="services-list field-error" aria-label={t("problemsTitle")}>
            {errors.map((m, i) => (
              <li key={i}>{m}</li>
            ))}
          </ul>
        ) : null}
        <div className="services-row">
          <FormField
            label={t("nameEn")}
            name="step-name-en"
            dir="ltr"
            lang="en"
            value={step.names?.en ?? ""}
            onChange={(e) =>
              updateStep(step.id, {
                names: { ...step.names, en: e.target.value } as Names,
              })
            }
          />
          <FormField
            label={t("nameAr")}
            name="step-name-ar"
            dir="rtl"
            lang="ar"
            value={step.names?.ar ?? ""}
            onChange={(e) =>
              updateStep(step.id, {
                names: { ...step.names, ar: e.target.value } as Names,
              })
            }
          />
        </div>
        {settings(step)}
        {step.type !== "end" ? (
          <fieldset className="route-conditions">
            <legend>{t("paths")}</legend>
            {out.length === 0 ? <p className="field-hint">{t("noPaths")}</p> : null}
            {out.map((l) => (
              <div key={l.to} className="route-path">
                <p>
                  <bdi>{t("pathTo", { name: nameOf(l.to) })}</bdi>{" "}
                  <Button variant="quiet" size="sm" onClick={() => setLinks(graph.links.filter((x) => x !== l))}>
                    {t("removePath", { name: nameOf(l.to) })}
                  </Button>
                </p>
                {branches(step.type) ? (
                  <ConditionsEditor
                    title={t("pathWhen", { name: nameOf(l.to) })}
                    conditions={l.when ?? []}
                    fields={fields}
                    onChange={(when) => setLinks(graph.links.map((x) => (x === l ? { ...x, when } : x)))}
                  />
                ) : null}
              </div>
            ))}
            {targets.length > 0 && (branches(step.type) || out.length === 0) ? (
              <SelectField
                label={t("addPath")}
                name="add-path"
                value=""
                onChange={(e) => connect(step.id, e.target.value)}
                options={[{ value: "", label: t("chooseStep") }, ...targets.map((s) => ({ value: s.id, label: nameOf(s.id) }))]}
              />
            ) : null}
          </fieldset>
        ) : null}
        <div className="services-actions">
          <Button variant="destructive" size="sm" onClick={() => remove(step.id)}>
            {t("removeStep")}
          </Button>
        </div>
      </>
    );
  }

  const general = problems.get("") ?? [];
  return (
    <div className="route-designer" data-testid="route-designer">
      {general.length > 0 ? (
        <Alert tone="warning" title={t("problemsTitle")}>
          <ul className="services-list">
            {general.map((m, i) => (
              <li key={i}>{m}</li>
            ))}
          </ul>
        </Alert>
      ) : null}
      <p className="visually-hidden" aria-live="polite">
        {notice}
      </p>
      <div className="route-designer-grid">
        <section className="route-palette" aria-labelledby="designer-palette">
          <h3 id="designer-palette">{t("palette")}</h3>
          <p className="field-hint">{current ? t("paletteHintAfter", { name: nameOf(current.id) }) : t("paletteHint")}</p>
          <ul className="route-palette-list">
            {STEP_TYPES.map((type) => (
              <li key={type}>
                <Button variant="quiet" size="sm" icon={STEP_ICONS[type]} onClick={() => add(type)} aria-label={t("add", { type: typeName(type) })}>
                  {typeName(type)}
                </Button>
              </li>
            ))}
          </ul>
        </section>
        <section className="route-canvas-section" aria-labelledby="designer-canvas">
          <h3 id="designer-canvas">{t("canvas")}</h3>
          <p className="field-hint">{t("canvasHint")}</p>
          <div className="route-canvas" dir="ltr">
            <ReactFlow
              nodes={nodes}
              edges={edges}
              nodeTypes={nodeTypes}
              onNodesChange={onNodesChange}
              onEdgesChange={(changes) => {
                for (const c of changes) {
                  if (c.type === "remove") setLinks(graph.links.filter((l) => `${l.from}->${l.to}` !== c.id));
                }
              }}
              onConnect={(c: Connection) => connect(c.source, c.target)}
              onPaneClick={() => setSelected(null)}
              fitView
              fitViewOptions={{ padding: 0.2, maxZoom: 1 }}
              minZoom={0.3}
              maxZoom={2}
              deleteKeyCode={["Delete"]}
              proOptions={{ hideAttribution: true }}
              ariaLabelConfig={{
                "node.a11yDescription.default": t("a11y.node"),
                "node.a11yDescription.keyboardDisabled": t("a11y.nodeNoKeyboard"),
                "node.a11yDescription.ariaLiveMessage": ({ direction, x, y }) =>
                  t("a11y.moved", {
                    direction: t(`a11y.directions.${direction}`),
                    x: String(Math.round(x)),
                    y: String(Math.round(y)),
                  }),
                "edge.a11yDescription.default": t("a11y.edge"),
                "controls.ariaLabel": t("a11y.controls"),
                "controls.zoomIn.ariaLabel": t("a11y.zoomIn"),
                "controls.zoomOut.ariaLabel": t("a11y.zoomOut"),
                "controls.fitView.ariaLabel": t("a11y.fitView"),
                "controls.interactive.ariaLabel": t("a11y.interactive"),
                "minimap.ariaLabel": t("a11y.minimap"),
                "handle.ariaLabel": t("a11y.handle"),
              }}
            >
              <Controls showInteractive={false} />
            </ReactFlow>
          </div>
        </section>
        <section className="route-properties" aria-labelledby={current ? "designer-properties-title" : "designer-properties"}>
          {current ? (
            stepPanel(current)
          ) : (
            <>
              <h3 id="designer-properties">{t("properties")}</h3>
              <p className="field-hint">{t("propertiesHint")}</p>
            </>
          )}
        </section>
      </div>
      <section aria-labelledby="designer-outline">
        <h3 id="designer-outline">{t("outline")}</h3>
        <p className="field-hint">{t("outlineHint")}</p>
        <ol className="services-list" data-testid="route-outline">
          {order.map(({ id }) => {
            const s = byId.get(id)!;
            const next = graph.links.filter((l) => l.from === id).map((l) => nameOf(l.to));
            const count = (problems.get(id) ?? []).length;
            return (
              <li key={id}>
                <Button variant="quiet" size="sm" onClick={() => setSelected(id)} aria-current={selected === id ? "true" : undefined}>
                  {t("typeAndName", {
                    type: typeName(s.type),
                    name: nameOf(id),
                  })}
                </Button>{" "}
                <span className="field-hint">
                  {next.length > 0
                    ? t("goesTo", {
                        names: format.list(next, { type: "conjunction" }),
                      })
                    : t("goesNowhere")}
                </span>
                {isHuman(s.type) && visited.has(id) ? (
                  <>
                    {" "}
                    <Badge tone="info">{t("reached")}</Badge>
                  </>
                ) : null}
                {count > 0 ? (
                  <>
                    {" "}
                    <Badge tone="danger">{t("problemCount", { count })}</Badge>
                  </>
                ) : null}
              </li>
            );
          })}
        </ol>
      </section>
    </div>
  );
}
