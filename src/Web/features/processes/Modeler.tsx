"use client";

import { useMemo, useState, type ReactNode } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import {
  Controls,
  Handle,
  MarkerType,
  Position,
  ReactFlow,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/base.css";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { IconButton } from "@/components/ui/IconButton";
import { Icon, type IconName } from "@/components/ui/Icon";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { problemMessages } from "@/lib/notifications/types";
import {
  EVENTS_FOR,
  PALETTE,
  TIMER_KINDS,
  blankElement,
  blankProcess,
  newElementId,
  readProcess,
  size,
  writeProcess,
  type ElementKind,
  type EventDefinition,
  type Mapping,
  type ModelElement,
  type ProcessDraft,
} from "@/lib/bpmn";
import type { ModelProblem, ProcessModelView } from "@/lib/processes";
import "./processes.css";

type ShapeData = { label: string; kind: ElementKind; event: EventDefinition; icon: IconName | null; invalid: boolean };
type ShapeNode = Node<ShapeData, "bpmn">;

const KIND_ICONS: Partial<Record<ElementKind, IconName>> = {
  userTask: "user",
  serviceTask: "settings",
  businessRuleTask: "policy",
  sendTask: "arrow-forward",
  receiveTask: "inbox",
  manualTask: "edit",
  callActivity: "link",
  exclusiveGateway: "close",
  parallelGateway: "plus",
  inclusiveGateway: "filter",
  eventBasedGateway: "bell",
};
const EVENT_ICONS: Partial<Record<EventDefinition, IconName>> = { timer: "clock", message: "inbox", error: "alert", escalation: "arrow-up", terminate: "stop" };

const isEvent = (kind: ElementKind) => kind.endsWith("Event");
const isGateway = (kind: ElementKind) => kind.endsWith("Gateway");
const isActivity = (kind: ElementKind) => !isEvent(kind) && !isGateway(kind);
const hasInputs = (kind: ElementKind) => kind === "serviceTask" || kind === "callActivity" || kind === "sendTask";
const hasOutputs = (kind: ElementKind) => kind === "serviceTask" || kind === "callActivity" || kind === "userTask";

/** One shape on the canvas: a circle for events, a diamond for gateways, a rounded box for activities. */
function Shape({ data, selected }: NodeProps<ShapeNode>) {
  const shape = isEvent(data.kind) ? "event" : isGateway(data.kind) ? "gateway" : "task";
  return (
    <div className="processes-node" data-shape={shape} data-kind={data.kind} data-invalid={data.invalid || undefined} data-selected={selected || undefined}>
      <Handle type="target" position={Position.Left} />
      <span className="processes-node-mark" aria-hidden="true">
        {data.icon ? <Icon name={data.icon} size={shape === "task" ? 14 : 16} mirrorInRtl={false} /> : null}
      </span>
      <bdi className="processes-node-label">{data.label}</bdi>
      {data.invalid ? (
        <span className="processes-node-problem" aria-hidden="true">
          <Icon name="alert" size={14} />
        </span>
      ) : null}
      <Handle type="source" position={Position.Right} />
    </div>
  );
}
const nodeTypes = { bpmn: Shape };

/** A visible table of rows, such as the inputs of a service task: two text fields per row and a remove button. */
function MappingList({
  title,
  rows,
  keyLabel,
  sourceLabel,
  addLabel,
  removeLabel,
  onChange,
}: {
  title: string;
  rows: Mapping[];
  keyLabel: (n: string) => string;
  sourceLabel: (n: string) => string;
  addLabel: string;
  removeLabel: (n: string) => string;
  onChange: (rows: Mapping[]) => void;
}) {
  return (
    <fieldset className="processes-mapping">
      <legend>{title}</legend>
      {rows.map((row, i) => (
        <div className="processes-filters" key={i}>
          <FormField label={keyLabel(String(i + 1))} name={`${title}-key-${i}`} dir="ltr" value={row.key} onChange={(e) => onChange(rows.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))} />
          <FormField
            label={sourceLabel(String(i + 1))}
            name={`${title}-source-${i}`}
            dir="ltr"
            value={row.source}
            onChange={(e) => onChange(rows.map((r, j) => (j === i ? { ...r, source: e.target.value } : r)))}
          />
          <IconButton icon="trash" label={removeLabel(String(i + 1))} onClick={() => onChange(rows.filter((_, j) => j !== i))} />
        </div>
      ))}
      <div>
        <Button variant="secondary" size="sm" onClick={() => onChange([...rows, { key: "", source: "" }])}>
          {addLabel}
        </Button>
      </div>
    </fieldset>
  );
}

/**
 * The process modeler of spec 024 (FR-001, FR-002, section 10) on React Flow (ADR-0017). It draws only the supported
 * subset, keeps TeamBeats settings in a properties panel per element, and offers every action from the keyboard: the
 * palette adds after the selected element, the panel connects and edits, the arrow keys move the focused shape and the
 * outline lists the model in order for screen readers. A model with elements the canvas cannot draw opens as XML.
 * Saving keeps a draft with the problems the server finds; publishing makes the next version. The server decides
 * what is valid and who may do what.
 */
export function Modeler({ processKey, initial, units }: { processKey: string | null; initial: ProcessModelView | null; units: OrgUnit[] }) {
  const t = useTranslations("processes.modeler");
  const locale = useLocale();
  const format = useFormatter();
  const [loaded] = useState(() => (initial ? readProcess(initial.bpmnXml) : null));
  const [key, setKey] = useState(processKey ?? "");
  const [view, setView] = useState(initial);
  const [owner, setOwner] = useState(initial?.ownerOrgUnitId ?? "");
  const [draft, setDraft] = useState<ProcessDraft>(() => loaded?.draft ?? blankProcess(""));
  const [xmlMode, setXmlMode] = useState(initial !== null && (loaded === null || loaded.unsupported.length > 0));
  const [xml, setXml] = useState(initial?.bpmnXml ?? "");
  const [unsupported, setUnsupported] = useState<string[]>(loaded?.unsupported ?? []);
  const [problems, setProblems] = useState<ModelProblem[]>(initial?.problems ?? []);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [connectTo, setConnectTo] = useState("");
  const [measured, setMeasured] = useState<Map<string, { width: number; height: number }>>(() => new Map());

  const kindName = (kind: ElementKind) => t(`kinds.${kind}`);
  const byId = useMemo(() => new Map(draft.elements.map((e) => [e.id, e])), [draft.elements]);
  const nameOf = (id: string) => {
    const e = byId.get(id);
    if (!e) return id;
    return (locale === "ar" ? e.nameAr || e.nameEn : e.nameEn || e.nameAr) || `${kindName(e.type)} (${e.id})`;
  };
  const problemFor = (id: string) => problems.filter((p) => p.elementId === id);
  const problemText = (p: ModelProblem) => p.message || p.key;
  const general = problems.filter((p) => !p.elementId || (!byId.has(p.elementId) && !draft.flows.some((f) => f.id === p.elementId)));

  const change = (update: (d: ProcessDraft) => ProcessDraft) => setDraft((d) => update(structuredClone(d)));
  const updateElement = (id: string, patch: Partial<ModelElement>) => change((d) => ({ ...d, elements: d.elements.map((e) => (e.id === id ? { ...e, ...patch } : e)) }));

  function connect(source: string, target: string) {
    if (!source || !target || source === target) return;
    change((d) => (d.flows.some((f) => f.source === source && f.target === target) ? d : { ...d, flows: [...d.flows, { id: newElementId("flow"), source, target, condition: "" }] }));
  }

  function add(kind: ElementKind) {
    const anchor = selected ? byId.get(selected) : undefined;
    const right = draft.elements.reduce((max, e) => Math.max(max, e.x), 0);
    const element =
      kind === "boundaryEvent" && anchor && isActivity(anchor.type)
        ? { ...blankElement(kind, anchor.x + size(anchor.type).width - 18, anchor.y + size(anchor.type).height - 18), attachedTo: anchor.id }
        : blankElement(kind, anchor ? anchor.x + 180 : right + 180, anchor ? anchor.y : 120);
    change((d) => {
      const flows = anchor && kind !== "boundaryEvent" && anchor.type !== "endEvent" ? [...d.flows, { id: newElementId("flow"), source: anchor.id, target: element.id, condition: "" }] : d.flows;
      return { ...d, elements: [...d.elements, element], flows };
    });
    setSelected(element.id);
    setNotice(t("added", { kind: kindName(kind) }));
  }

  function remove(id: string) {
    change((d) => {
      const gone = new Set([id, ...d.elements.filter((e) => e.attachedTo === id).map((e) => e.id)]);
      return {
        ...d,
        elements: d.elements.filter((e) => !gone.has(e.id)).map((e) => (e.defaultFlow && d.flows.some((f) => f.id === e.defaultFlow && (gone.has(f.source) || gone.has(f.target))) ? { ...e, defaultFlow: "" } : e)),
        flows: d.flows.filter((f) => !gone.has(f.source) && !gone.has(f.target)),
      };
    });
    setSelected(null);
  }

  function removeFlow(id: string) {
    change((d) => ({ ...d, flows: d.flows.filter((f) => f.id !== id), elements: d.elements.map((e) => (e.defaultFlow === id ? { ...e, defaultFlow: "" } : e)) }));
    if (selected === id) setSelected(null);
  }

  const nodes: ShapeNode[] = useMemo(
    () =>
      draft.elements.map((e) => {
        const { width, height } = size(e.type);
        const label = (locale === "ar" ? e.nameAr || e.nameEn : e.nameEn || e.nameAr) || "";
        return {
          id: e.id,
          type: "bpmn",
          position: { x: e.x, y: e.y },
          width,
          height,
          measured: measured.get(e.id) ?? { width, height },
          selected: selected === e.id,
          ariaLabel: label ? t("kindAndName", { kind: t(`kinds.${e.type}`), name: label }) : t(`kinds.${e.type}`),
          zIndex: e.type === "boundaryEvent" ? 1 : 0,
          data: {
            label,
            kind: e.type,
            event: e.event,
            icon: (isEvent(e.type) ? EVENT_ICONS[e.event] : KIND_ICONS[e.type]) ?? null,
            invalid: problems.some((p) => p.elementId === e.id),
          },
        };
      }),
    [draft.elements, measured, selected, problems, locale, t],
  );

  const edges: Edge[] = useMemo(
    () =>
      draft.flows.map((f) => {
        const source = byId.get(f.source);
        const isDefault = source?.defaultFlow === f.id;
        return {
          id: f.id,
          source: f.source,
          target: f.target,
          label: f.condition || (isDefault ? t("defaultFlow") : undefined),
          markerEnd: { type: MarkerType.ArrowClosed, color: "var(--tb-color-text-secondary)" },
          selected: selected === f.id,
          ariaLabel: t("flowFromTo", { from: nameOf(f.source), to: nameOf(f.target) }),
          className: problems.some((p) => p.elementId === f.id) ? "processes-edge-invalid" : undefined,
        };
      }),
    // nameOf reads byId and locale, both listed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [draft.flows, byId, selected, problems, locale, t],
  );

  function onNodesChange(changes: NodeChange<ShapeNode>[]) {
    for (const c of changes) {
      if (c.type === "position" && c.position) {
        const { x, y } = c.position;
        updateElement(c.id, { x: Math.round(x), y: Math.round(y) });
      } else if (c.type === "select") {
        setSelected((s) => (c.selected ? c.id : s === c.id ? null : s));
      } else if (c.type === "remove") {
        remove(c.id);
      } else if (c.type === "dimensions" && c.dimensions) {
        const dims = c.dimensions;
        setMeasured((m) => new Map(m).set(c.id, dims));
      }
    }
  }

  function onEdgesChange(changes: EdgeChange[]) {
    for (const c of changes) {
      if (c.type === "select") setSelected((s) => (c.selected ? c.id : s === c.id ? null : s));
      else if (c.type === "remove") removeFlow(c.id);
    }
  }

  async function call(name: string, url: string, init: RequestInit): Promise<Response | null> {
    setBusy(name);
    setMessages([]);
    setNotice(null);
    try {
      const response = await fetch(url, { ...init, headers: { "content-type": "application/json" } });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return null;
      }
      return response;
    } catch {
      setMessages([t("failed")]);
      return null;
    } finally {
      setBusy(null);
    }
  }

  async function save() {
    const bpmnXml = xmlMode ? xml : writeProcess(draft);
    const response = await call("save", `/api/v1/workflow/processes/${encodeURIComponent(key.trim())}`, {
      method: "PUT",
      body: JSON.stringify({ bpmnXml, ownerOrgUnitId: owner || null }),
    });
    if (!response) return;
    const saved = (await response.json()) as ProcessModelView;
    setView(saved);
    setProblems(saved.problems);
    if (xmlMode) setXml(saved.bpmnXml);
    setNotice(saved.problems.length === 0 ? t("saved") : t("savedWithProblems", { count: saved.problems.length }));
    if (!processKey) window.history.replaceState(null, "", `/admin/processes/${encodeURIComponent(saved.key)}`);
  }

  async function publish() {
    const response = await call("publish", `/api/v1/workflow/processes/${encodeURIComponent(view!.key)}:publish`, { method: "POST" });
    if (!response) return;
    const published = (await response.json()) as ProcessModelView;
    setView(published);
    setProblems(published.problems);
    setNotice(t("published", { version: String(published.versions[0]?.version ?? 1) }));
  }

  function showXml() {
    setXml(writeProcess(draft));
    setXmlMode(true);
    setMessages([]);
  }

  function showDiagram() {
    const read = readProcess(xml);
    if (!read) {
      setMessages([t("xmlUnreadable")]);
      return;
    }
    setUnsupported(read.unsupported);
    if (read.unsupported.length > 0) {
      setMessages([t("xmlUnsupported", { elements: read.unsupported.join(", ") })]);
      return;
    }
    setDraft(read.draft);
    setSelected(null);
    setXmlMode(false);
    setMessages([]);
  }

  const outline = useMemo(() => {
    const order: string[] = [];
    const seen = new Set<string>();
    const queue = draft.elements.filter((e) => e.type === "startEvent").map((e) => e.id);
    while (queue.length > 0) {
      const id = queue.shift()!;
      if (seen.has(id)) continue;
      seen.add(id);
      order.push(id);
      for (const e of draft.elements.filter((x) => x.attachedTo === id)) queue.push(e.id);
      for (const f of draft.flows.filter((x) => x.source === id)) queue.push(f.target);
    }
    for (const e of draft.elements) if (!seen.has(e.id)) order.push(e.id);
    return order;
  }, [draft.elements, draft.flows]);

  const current = selected ? byId.get(selected) : undefined;
  const currentFlow = selected && !current ? draft.flows.find((f) => f.id === selected) : undefined;

  function elementPanel(e: ModelElement): ReactNode {
    const set = (patch: Partial<ModelElement>) => updateElement(e.id, patch);
    const events = EVENTS_FOR[e.type];
    const outgoing = draft.flows.filter((f) => f.source === e.id);
    const errors = problemFor(e.id);
    const targets = draft.elements.filter((x) => x.id !== e.id && !outgoing.some((f) => f.target === x.id) && x.type !== "startEvent" && x.type !== "boundaryEvent");
    const text = (name: keyof ModelElement, label: string, extra: { hint?: string; dir?: "ltr" | "rtl"; inputMode?: "numeric" } = {}) => (
      <FormField label={label} name={`${e.id}-${name}`} value={String(e[name] ?? "")} onChange={(ev) => set({ [name]: ev.target.value } as Partial<ModelElement>)} {...extra} />
    );
    return (
      <>
        <h3 id="modeler-properties-title">
          {kindName(e.type)} <bdi className="processes-code">{e.id}</bdi>
        </h3>
        {errors.length > 0 ? (
          <ul className="processes-list field-error" aria-label={t("problemsTitle")}>
            {errors.map((p, i) => (
              <li key={i}>{problemText(p)}</li>
            ))}
          </ul>
        ) : null}
        {text("nameEn", t("nameEn"))}
        {text("nameAr", t("nameAr"), { dir: "rtl" })}
        {events ? (
          <SelectField
            label={t("event")}
            name={`${e.id}-event`}
            value={e.event}
            onChange={(ev) => set({ event: ev.target.value as EventDefinition })}
            options={events.map((x) => ({ value: x, label: t(`events.${x}`) }))}
          />
        ) : null}
        {e.event === "timer" ? (
          <>
            <SelectField
              label={t("timerKind")}
              name={`${e.id}-timerKind`}
              value={e.timerKind}
              onChange={(ev) => set({ timerKind: ev.target.value as ModelElement["timerKind"] })}
              options={TIMER_KINDS.map((x) => ({ value: x, label: t(`timerKinds.${x}`) }))}
            />
            {text("timerValue", t("timerValue"), { hint: t(`timerHints.${e.timerKind}`), dir: "ltr" })}
          </>
        ) : null}
        {(events && e.event === "message") || e.type === "sendTask" || e.type === "receiveTask" ? (
          <>
            {text("message", t("message"), { hint: t("messageHint"), dir: "ltr" })}
            {text("correlationKey", t("correlationKey"), { hint: t("correlationKeyHint"), dir: "ltr" })}
          </>
        ) : null}
        {e.event === "error" || e.event === "escalation" ? text("code", t("code"), { dir: "ltr" }) : null}
        {e.type === "boundaryEvent" ? (
          <>
            <SelectField
              label={t("attachedTo")}
              name={`${e.id}-attachedTo`}
              value={e.attachedTo}
              onChange={(ev) => set({ attachedTo: ev.target.value })}
              options={[{ value: "", label: t("choose") }, ...draft.elements.filter((x) => isActivity(x.type)).map((x) => ({ value: x.id, label: nameOf(x.id) }))]}
            />
            <Checkbox label={t("interrupting")} hint={t("interruptingHint")} checked={e.interrupting} onChange={(ev) => set({ interrupting: ev.target.checked })} />
          </>
        ) : null}
        {e.type === "userTask" ? (
          <>
            {text("users", t("users"), { hint: t("usersHint"), dir: "ltr" })}
            {text("permission", t("permission"), { hint: t("permissionHint"), dir: "ltr" })}
            <Checkbox label={t("lineManager")} checked={e.lineManager} onChange={(ev) => set({ lineManager: ev.target.checked })} />
            {text("managerLevel", t("managerLevel"), { hint: t("managerLevelHint"), inputMode: "numeric", dir: "ltr" })}
            {text("assignee", t("assignee"), { hint: t("assigneeHint"), dir: "ltr" })}
            {text("fallbackUsers", t("fallbackUsers"), { hint: t("usersHint"), dir: "ltr" })}
            {text("dueWorkingDays", t("dueWorkingDays"), { inputMode: "numeric", dir: "ltr" })}
          </>
        ) : null}
        {e.type === "serviceTask" ? (
          <>
            <SelectField
              label={t("serviceKind")}
              name={`${e.id}-serviceKind`}
              value={e.serviceKind}
              onChange={(ev) => set({ serviceKind: ev.target.value as ModelElement["serviceKind"] })}
              options={[
                { value: "connector", label: t("serviceKinds.connector") },
                { value: "webhook", label: t("serviceKinds.webhook") },
              ]}
            />
            {e.serviceKind === "connector" ? (
              <>
                {text("connection", t("connection"), { hint: t("connectionHint"), dir: "ltr" })}
                {text("action", t("action"), { dir: "ltr" })}
              </>
            ) : (
              text("webhook", t("webhook"), { hint: t("webhookHint"), dir: "ltr" })
            )}
            {text("retries", t("retries"), { inputMode: "numeric", dir: "ltr" })}
          </>
        ) : null}
        {e.type === "businessRuleTask" ? (
          <>
            {text("decisionKey", t("decisionKey"), { dir: "ltr" })}
            {text("resultVariable", t("resultVariable"), { dir: "ltr" })}
          </>
        ) : null}
        {e.type === "callActivity" ? text("calledKey", t("calledKey"), { hint: t("calledKeyHint"), dir: "ltr" }) : null}
        {hasInputs(e.type) ? (
          <MappingList
            title={t("inputs")}
            rows={e.inputs}
            keyLabel={(n) => t("inputName", { number: n })}
            sourceLabel={(n) => t("inputSource", { number: n })}
            addLabel={t("addInput")}
            removeLabel={(n) => t("removeInput", { number: n })}
            onChange={(rows) => set({ inputs: rows })}
          />
        ) : null}
        {hasOutputs(e.type) ? (
          <MappingList
            title={t("outputs")}
            rows={e.outputs}
            keyLabel={(n) => t("outputVariable", { number: n })}
            sourceLabel={(n) => t("outputSource", { number: n })}
            addLabel={t("addOutput")}
            removeLabel={(n) => t("removeOutput", { number: n })}
            onChange={(rows) => set({ outputs: rows })}
          />
        ) : null}
        {isActivity(e.type) ? (
          <fieldset className="processes-mapping">
            <legend>{t("loopTitle")}</legend>
            <Checkbox label={t("loop")} hint={t("loopHint")} checked={e.loop} onChange={(ev) => set({ loop: ev.target.checked })} />
            {e.loop ? (
              <>
                <Checkbox label={t("loopSequential")} checked={e.loopSequential} onChange={(ev) => set({ loopSequential: ev.target.checked })} />
                {text("loopCollection", t("loopCollection"), { dir: "ltr" })}
                {text("loopVariable", t("loopVariable"), { dir: "ltr" })}
                {text("loopCompletion", t("loopCompletion"), { hint: t("feelHint"), dir: "ltr" })}
              </>
            ) : null}
          </fieldset>
        ) : null}
        {e.type === "exclusiveGateway" || e.type === "inclusiveGateway" ? (
          <SelectField
            label={t("defaultFlowField")}
            hint={t("defaultFlowHint")}
            name={`${e.id}-default`}
            value={e.defaultFlow}
            onChange={(ev) => set({ defaultFlow: ev.target.value })}
            options={[{ value: "", label: t("none") }, ...outgoing.map((f) => ({ value: f.id, label: t("flowTo", { to: nameOf(f.target) }) }))]}
          />
        ) : null}
        {e.type !== "endEvent" ? (
          <fieldset className="processes-mapping">
            <legend>{t("outgoing")}</legend>
            {outgoing.length === 0 ? <p className="field-hint">{t("noOutgoing")}</p> : null}
            {outgoing.map((f) => (
              <div className="processes-filters" key={f.id}>
                <span>
                  <bdi>{t("flowTo", { to: nameOf(f.target) })}</bdi>
                </span>
                {isGateway(e.type) ? (
                  <FormField
                    label={t("condition", { to: nameOf(f.target) })}
                    hint={t("feelHint")}
                    name={`${f.id}-condition`}
                    dir="ltr"
                    value={f.condition}
                    error={problemFor(f.id).map(problemText).join(" ") || null}
                    onChange={(ev) => change((d) => ({ ...d, flows: d.flows.map((x) => (x.id === f.id ? { ...x, condition: ev.target.value } : x)) }))}
                  />
                ) : null}
                <IconButton icon="trash" label={t("removeFlowTo", { to: nameOf(f.target) })} onClick={() => removeFlow(f.id)} />
              </div>
            ))}
            {targets.length > 0 ? (
              <div className="processes-filters">
                <SelectField
                  label={t("connectTo")}
                  name={`${e.id}-connect`}
                  value={connectTo}
                  onChange={(ev) => setConnectTo(ev.target.value)}
                  options={[{ value: "", label: t("choose") }, ...targets.map((x) => ({ value: x.id, label: nameOf(x.id) }))]}
                />
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={!connectTo}
                  onClick={() => {
                    connect(e.id, connectTo);
                    setConnectTo("");
                  }}
                >
                  {t("connect")}
                </Button>
              </div>
            ) : null}
          </fieldset>
        ) : null}
        <div className="processes-actions">
          <Button variant="secondary" size="sm" onClick={() => remove(e.id)}>
            {t("removeElement")}
          </Button>
          <Button variant="quiet" size="sm" onClick={() => setSelected(null)}>
            {t("deselect")}
          </Button>
        </div>
      </>
    );
  }

  return (
    <div className="processes">
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
      {problems.length > 0 ? (
        <Alert tone="warning" title={t("problemsTitle")}>
          <ul className="processes-list">
            {general.map((p, i) => (
              <li key={`g${i}`}>{problemText(p)}</li>
            ))}
            {problems
              .filter((p) => !general.includes(p))
              .map((p, i) => (
                <li key={`e${i}`}>
                  {xmlMode ? (
                    <bdi>{t("problemOn", { name: nameOf(p.elementId), problem: problemText(p) })}</bdi>
                  ) : (
                    <Button variant="quiet" size="sm" onClick={() => setSelected(p.elementId)}>
                      {t("problemOn", { name: nameOf(p.elementId), problem: problemText(p) })}
                    </Button>
                  )}
                </li>
              ))}
          </ul>
        </Alert>
      ) : null}

      <section className="processes-section" aria-labelledby="modeler-settings">
        <h2 id="modeler-settings">{t("settings")}</h2>
        <div className="processes-filters">
          <FormField label={t("key")} hint={t("keyHint")} name="key" value={key} onChange={(e) => setKey(e.target.value)} readOnly={processKey !== null} maxLength={64} dir="ltr" />
          <FormField label={t("processNameEn")} name="processNameEn" value={draft.nameEn} disabled={xmlMode} onChange={(e) => change((d) => ({ ...d, nameEn: e.target.value }))} maxLength={200} />
          <FormField
            label={t("processNameAr")}
            name="processNameAr"
            dir="rtl"
            value={draft.nameAr}
            disabled={xmlMode}
            onChange={(e) => change((d) => ({ ...d, nameAr: e.target.value }))}
            maxLength={200}
          />
          <SelectField
            label={t("owner")}
            name="owner"
            value={owner}
            onChange={(e) => setOwner(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitName(u, locale) }))]}
          />
        </div>
        <div className="processes-actions">
          <Button onClick={save} disabled={busy !== null || !key.trim()}>
            {t("save")}
          </Button>
          <Button variant="secondary" onClick={publish} disabled={busy !== null || !view || problems.length > 0}>
            {t("publish")}
          </Button>
          {xmlMode ? (
            <Button variant="quiet" onClick={showDiagram}>
              {t("showDiagram")}
            </Button>
          ) : (
            <Button variant="quiet" onClick={showXml}>
              {t("showXml")}
            </Button>
          )}
        </div>
      </section>

      {xmlMode ? (
        <section className="processes-section" aria-labelledby="modeler-xml">
          <h2 id="modeler-xml">{t("xmlTitle")}</h2>
          {unsupported.length > 0 ? (
            <Alert tone="info" title={t("unsupportedTitle")}>
              {t("unsupportedBody", { elements: unsupported.join(", ") })}
            </Alert>
          ) : null}
          <TextAreaField label={t("xml")} name="bpmnXml" dir="ltr" rows={20} className="processes-code" value={xml} onChange={(e) => setXml(e.target.value)} spellCheck={false} />
        </section>
      ) : (
        <div className="processes-modeler">
          <section className="processes-section processes-palette" aria-labelledby="modeler-palette">
            <h2 id="modeler-palette">{t("palette")}</h2>
            <p className="field-hint">{current ? t("paletteHintAfter", { name: nameOf(current.id) }) : t("paletteHint")}</p>
            <ul className="processes-palette-list">
              {PALETTE.map((kind) => (
                <li key={kind}>
                  <Button variant="quiet" size="sm" icon={KIND_ICONS[kind]} onClick={() => add(kind)} aria-label={t("add", { kind: kindName(kind) })}>
                    {kindName(kind)}
                  </Button>
                </li>
              ))}
            </ul>
          </section>

          <section className="processes-section processes-canvas-section" aria-labelledby="modeler-canvas">
            <h2 id="modeler-canvas">{t("canvas")}</h2>
            <p className="field-hint">{t("canvasHint")}</p>
            <div className="processes-canvas" dir="ltr">
              <ReactFlow
                nodes={nodes}
                edges={edges}
                nodeTypes={nodeTypes}
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                onConnect={(c: Connection) => connect(c.source, c.target)}
                onPaneClick={() => setSelected(null)}
                fitView
                fitViewOptions={{ padding: 0.2, maxZoom: 1 }}
                minZoom={0.3}
                maxZoom={2}
                snapToGrid
                snapGrid={[10, 10]}
                deleteKeyCode={["Delete"]}
                proOptions={{ hideAttribution: true }}
                ariaLabelConfig={{
                  "node.a11yDescription.default": t("a11y.node"),
                  "node.a11yDescription.keyboardDisabled": t("a11y.nodeNoKeyboard"),
                  "node.a11yDescription.ariaLiveMessage": ({ direction, x, y }) => t("a11y.moved", { direction: t(`a11y.directions.${direction}`), x: String(Math.round(x)), y: String(Math.round(y)) }),
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

          <section className="processes-section processes-properties" aria-labelledby={current ? "modeler-properties-title" : "modeler-properties"}>
            {current ? (
              elementPanel(current)
            ) : currentFlow ? (
              <>
                <h3 id="modeler-properties-title">{t("flowFromTo", { from: nameOf(currentFlow.source), to: nameOf(currentFlow.target) })}</h3>
                {problemFor(currentFlow.id).length > 0 ? <p className="field-error">{problemFor(currentFlow.id).map(problemText).join(" ")}</p> : null}
                <FormField
                  label={t("condition", { to: nameOf(currentFlow.target) })}
                  hint={t("feelHint")}
                  name="flow-condition"
                  dir="ltr"
                  value={currentFlow.condition}
                  onChange={(ev) => change((d) => ({ ...d, flows: d.flows.map((x) => (x.id === currentFlow.id ? { ...x, condition: ev.target.value } : x)) }))}
                />
                <div className="processes-actions">
                  <Button variant="secondary" size="sm" onClick={() => removeFlow(currentFlow.id)}>
                    {t("removeFlow")}
                  </Button>
                </div>
              </>
            ) : (
              <>
                <h2 id="modeler-properties">{t("properties")}</h2>
                <p className="field-hint">{t("propertiesHint")}</p>
              </>
            )}
          </section>
        </div>
      )}

      {!xmlMode ? (
        <section className="processes-section" aria-labelledby="modeler-outline">
          <h2 id="modeler-outline">{t("outline")}</h2>
          <p className="field-hint">{t("outlineHint")}</p>
          <ol className="processes-list">
            {outline.map((id) => {
              const e = byId.get(id)!;
              const next = draft.flows.filter((f) => f.source === id).map((f) => nameOf(f.target));
              return (
                <li key={id}>
                  <Button variant="quiet" size="sm" onClick={() => setSelected(id)} aria-current={selected === id ? "true" : undefined}>
                    {t("kindAndName", { kind: kindName(e.type), name: nameOf(id) })}
                  </Button>{" "}
                  <span className="field-hint">
                    {e.type === "boundaryEvent" && e.attachedTo ? `${t("onElement", { name: nameOf(e.attachedTo) })} ` : ""}
                    {next.length > 0 ? t("goesTo", { names: format.list(next, { type: "conjunction" }) }) : t("goesNowhere")}
                  </span>
                  {problemFor(id).length > 0 ? (
                    <>
                      {" "}
                      <Badge tone="danger">{t("problemCount", { count: problemFor(id).length })}</Badge>
                    </>
                  ) : null}
                </li>
              );
            })}
          </ol>
        </section>
      ) : null}

      {view && view.versions.length > 0 ? (
        <section className="processes-section" aria-labelledby="modeler-versions">
          <h2 id="modeler-versions">{t("versions")}</h2>
          <ul className="processes-list">
            {view.versions.map((v) => (
              <li key={v.version}>
                {t("versionLine", {
                  version: String(v.version),
                  date: format.dateTime(new Date(v.publishedAt), { dateStyle: "medium", timeStyle: "short" }),
                  active: String(v.activeInstances),
                })}
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}
