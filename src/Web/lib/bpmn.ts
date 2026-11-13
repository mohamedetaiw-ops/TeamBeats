/**
 * Reads and writes the process models of spec 024 (BPMN 2.0 with the TeamBeats extension namespace, section 12) with
 * the browser's own XML parser, so the modeler works on plain values. Diagram positions round-trip through BPMN
 * diagram interchange. The server reads the same XML again and decides whether a model is valid; nothing here judges it.
 */
export const BPMN_NS = "http://www.omg.org/spec/BPMN/20100524/MODEL";
export const BPMNDI_NS = "http://www.omg.org/spec/BPMN/20100524/DI";
export const DC_NS = "http://www.omg.org/spec/DD/20100524/DC";
export const DI_NS = "http://www.omg.org/spec/DD/20100524/DI";
export const TB_NS = "https://teambeats.example/schema/bpmn/1.0";
const XML_NS = "http://www.w3.org/XML/1998/namespace";
const XMLNS = "http://www.w3.org/2000/xmlns/";

/** The elements the palette offers: the R2 subset the modeler draws. Sub-processes are edited as XML. */
export const PALETTE = [
  "startEvent",
  "endEvent",
  "userTask",
  "serviceTask",
  "businessRuleTask",
  "sendTask",
  "receiveTask",
  "manualTask",
  "exclusiveGateway",
  "parallelGateway",
  "inclusiveGateway",
  "eventBasedGateway",
  "intermediateCatchEvent",
  "intermediateThrowEvent",
  "boundaryEvent",
  "callActivity",
] as const;
export type ElementKind = (typeof PALETTE)[number];

export const EVENT_DEFINITIONS = ["none", "timer", "message", "error", "escalation", "terminate"] as const;
export type EventDefinition = (typeof EVENT_DEFINITIONS)[number];

/** Which event definitions each event kind can carry in the subset. */
export const EVENTS_FOR: Partial<Record<ElementKind, readonly EventDefinition[]>> = {
  startEvent: ["none", "timer", "message"],
  endEvent: ["none", "terminate", "error", "escalation", "message"],
  intermediateCatchEvent: ["timer", "message"],
  intermediateThrowEvent: ["message", "escalation"],
  boundaryEvent: ["timer", "message", "error", "escalation"],
};

export const TIMER_KINDS = ["duration", "date", "cycle", "workingDays"] as const;
export type TimerKind = (typeof TIMER_KINDS)[number];

export type Mapping = { key: string; source: string };

export type ModelElement = {
  id: string;
  type: ElementKind;
  nameEn: string;
  nameAr: string;
  x: number;
  y: number;
  event: EventDefinition;
  timerKind: TimerKind;
  timerValue: string;
  message: string;
  correlationKey: string;
  code: string;
  attachedTo: string;
  interrupting: boolean;
  defaultFlow: string;
  users: string;
  permission: string;
  lineManager: boolean;
  managerLevel: string;
  assignee: string;
  fallbackUsers: string;
  dueWorkingDays: string;
  serviceKind: "connector" | "webhook";
  connection: string;
  action: string;
  webhook: string;
  retries: string;
  inputs: Mapping[];
  outputs: Mapping[];
  decisionKey: string;
  resultVariable: string;
  calledKey: string;
  loop: boolean;
  loopSequential: boolean;
  loopCollection: string;
  loopVariable: string;
  loopCompletion: string;
};

export type ModelFlow = { id: string; source: string; target: string; condition: string };

export type ProcessDraft = { processId: string; nameEn: string; nameAr: string; elements: ModelElement[]; flows: ModelFlow[] };

export type ReadResult = { draft: ProcessDraft; unsupported: string[] } | null;

const STRUCTURAL = new Set(["extensionElements", "incoming", "outgoing", "documentation", "laneSet", "textAnnotation", "association", "dataObject", "dataObjectReference"]);

export function size(type: ElementKind): { width: number; height: number } {
  if (type.endsWith("Event")) return { width: 36, height: 36 };
  if (type.endsWith("Gateway")) return { width: 50, height: 50 };
  return { width: 120, height: 80 };
}

let counter = 0;
/** A new element ID: a letter first, then letters, digits and underscores, as the server expects. */
export function newElementId(prefix: string): string {
  counter += 1;
  return `${prefix}_${Date.now().toString(36)}${counter.toString(36)}`;
}

export function blankElement(type: ElementKind, x: number, y: number): ModelElement {
  const events = EVENTS_FOR[type];
  return {
    id: newElementId(type.replace(/[A-Z].*$/, "") || "element"),
    type,
    nameEn: "",
    nameAr: "",
    x,
    y,
    event: events ? events[0]! : "none",
    timerKind: "duration",
    timerValue: "",
    message: "",
    correlationKey: "",
    code: "",
    attachedTo: "",
    interrupting: true,
    defaultFlow: "",
    users: "",
    permission: "",
    lineManager: false,
    managerLevel: "",
    assignee: "",
    fallbackUsers: "",
    dueWorkingDays: "",
    serviceKind: "connector",
    connection: "",
    action: "",
    webhook: "",
    retries: "",
    inputs: [],
    outputs: [],
    decisionKey: "",
    resultVariable: "",
    calledKey: "",
    loop: false,
    loopSequential: false,
    loopCollection: "",
    loopVariable: "",
    loopCompletion: "",
  };
}

export function blankProcess(name: string): ProcessDraft {
  const start = { ...blankElement("startEvent", 80, 120), id: "start" };
  const end = { ...blankElement("endEvent", 360, 120), id: "end" };
  return { processId: "process", nameEn: name, nameAr: "", elements: [start, end], flows: [{ id: "flow_start_end", source: "start", target: "end", condition: "" }] };
}

const bpmnChildren = (parent: Element, name?: string) =>
  Array.from(parent.children).filter((c) => c.namespaceURI === BPMN_NS && (name === undefined || c.localName === name));
const ext = (el: Element, name: string) =>
  bpmnChildren(el, "extensionElements").flatMap((e) => Array.from(e.children).filter((c) => c.namespaceURI === TB_NS && c.localName === name));
const attr = (el: Element | undefined, name: string) => el?.getAttribute(name)?.trim() ?? "";

function arabicName(el: Element): string {
  return ext(el, "name").find((n) => n.getAttributeNS(XML_NS, "lang") === "ar")?.textContent?.trim() ?? "";
}

function englishName(el: Element): string {
  return ext(el, "name").find((n) => n.getAttributeNS(XML_NS, "lang") === "en")?.textContent?.trim() || attr(el, "name");
}

/** The draft in a BPMN file, and the elements the modeler cannot draw; null when the file cannot be read at all. */
export function readProcess(xml: string): ReadResult {
  if (!xml.trim()) return null;
  const doc = new DOMParser().parseFromString(xml, "application/xml");
  if (doc.getElementsByTagName("parsererror").length > 0) return null;
  const root = doc.documentElement;
  const process = bpmnChildren(root, "process")[0];
  if (!process) return null;
  const messages = new Map(bpmnChildren(root, "message").map((m) => [attr(m, "id"), attr(m, "name") || attr(m, "id")]));
  const errors = new Map(bpmnChildren(root, "error").map((m) => [attr(m, "id"), attr(m, "errorCode")]));
  const escalations = new Map(bpmnChildren(root, "escalation").map((m) => [attr(m, "id"), attr(m, "escalationCode")]));
  const bounds = new Map<string, { x: number; y: number }>();
  for (const shape of Array.from(doc.getElementsByTagNameNS(BPMNDI_NS, "BPMNShape"))) {
    const b = shape.getElementsByTagNameNS(DC_NS, "Bounds")[0];
    if (b) bounds.set(attr(shape, "bpmnElement"), { x: Number(b.getAttribute("x")) || 0, y: Number(b.getAttribute("y")) || 0 });
  }
  const unsupported: string[] = [];
  const elements: ModelElement[] = [];
  const flows: ModelFlow[] = [];
  for (const child of bpmnChildren(process)) {
    const local = child.localName;
    const id = attr(child, "id");
    if (local === "sequenceFlow") {
      flows.push({ id, source: attr(child, "sourceRef"), target: attr(child, "targetRef"), condition: bpmnChildren(child, "conditionExpression")[0]?.textContent?.trim() ?? "" });
      continue;
    }
    if (STRUCTURAL.has(local)) continue;
    if (!(PALETTE as readonly string[]).includes(local)) {
      unsupported.push(id || local);
      continue;
    }
    const type = local as ElementKind;
    const e = blankElement(type, 0, 0);
    e.id = id;
    e.nameEn = englishName(child);
    e.nameAr = arabicName(child);
    const timer = bpmnChildren(child, "timerEventDefinition")[0];
    const message = bpmnChildren(child, "messageEventDefinition")[0];
    const error = bpmnChildren(child, "errorEventDefinition")[0];
    const escalation = bpmnChildren(child, "escalationEventDefinition")[0];
    if (timer) {
      e.event = "timer";
      const days = attr(ext(child, "timer")[0], "workingDays");
      const date = bpmnChildren(timer, "timeDate")[0]?.textContent?.trim();
      const duration = bpmnChildren(timer, "timeDuration")[0]?.textContent?.trim();
      const cycle = bpmnChildren(timer, "timeCycle")[0]?.textContent?.trim();
      [e.timerKind, e.timerValue] = days ? ["workingDays", days] : date ? ["date", date] : cycle ? ["cycle", cycle] : ["duration", duration ?? ""];
    } else if (message) {
      e.event = "message";
      e.message = messages.get(attr(message, "messageRef")) ?? "";
    } else if (error) {
      e.event = "error";
      e.code = errors.get(attr(error, "errorRef")) ?? "";
    } else if (escalation) {
      e.event = "escalation";
      e.code = escalations.get(attr(escalation, "escalationRef")) ?? "";
    } else if (bpmnChildren(child, "terminateEventDefinition").length > 0) {
      e.event = "terminate";
    } else if (EVENTS_FOR[type]) {
      e.event = "none";
    }
    if (type === "sendTask" || type === "receiveTask") e.message = messages.get(attr(child, "messageRef")) ?? "";
    e.correlationKey = attr(ext(child, "correlation")[0], "key");
    e.attachedTo = attr(child, "attachedToRef");
    e.interrupting = type === "boundaryEvent" ? attr(child, "cancelActivity") !== "false" : attr(child, "isInterrupting") !== "false";
    e.defaultFlow = attr(child, "default");
    const a = ext(child, "assignment")[0];
    e.users = attr(a, "users");
    e.permission = attr(a, "permission");
    e.lineManager = attr(a, "lineManager") === "true";
    e.managerLevel = attr(a, "managerLevel");
    e.assignee = attr(a, "assignee");
    e.fallbackUsers = attr(a, "fallbackUsers");
    e.dueWorkingDays = attr(ext(child, "due")[0], "workingDays");
    const connector = ext(child, "connector")[0];
    const webhook = ext(child, "webhook")[0];
    e.serviceKind = webhook ? "webhook" : "connector";
    e.connection = attr(connector, "connection");
    e.action = attr(connector, "action");
    e.webhook = attr(webhook, "id");
    e.retries = attr(connector ?? webhook, "retries");
    e.inputs = ext(child, "input").map((m) => ({ key: attr(m, "name"), source: attr(m, "source") }));
    e.outputs = ext(child, "output").map((m) => ({ key: attr(m, "variable"), source: attr(m, "source") }));
    const decision = ext(child, "decision")[0];
    e.decisionKey = attr(decision, "key");
    e.resultVariable = attr(decision, "resultVariable");
    e.calledKey = attr(child, "calledElement");
    const loop = bpmnChildren(child, "multiInstanceLoopCharacteristics")[0];
    if (loop) {
      const settings = ext(child, "loop")[0];
      e.loop = true;
      e.loopSequential = attr(loop, "isSequential") === "true";
      e.loopCollection = attr(settings, "collection");
      e.loopVariable = attr(settings, "elementVariable");
      e.loopCompletion = bpmnChildren(loop, "completionCondition")[0]?.textContent?.trim() ?? "";
    }
    const at = bounds.get(id);
    if (at) {
      e.x = at.x;
      e.y = at.y;
    }
    elements.push(e);
  }
  layoutMissing(elements, flows, new Set(bounds.keys()));
  return { draft: { processId: attr(process, "id") || "process", nameEn: englishName(process), nameAr: arabicName(process), elements, flows }, unsupported };
}

/** Places elements that have no diagram position in columns by their distance from a start event. */
function layoutMissing(elements: ModelElement[], flows: ModelFlow[], placed: Set<string>) {
  if (elements.every((e) => placed.has(e.id))) return;
  const depth = new Map<string, number>();
  const queue = elements.filter((e) => e.type === "startEvent").map((e) => e.id);
  queue.forEach((id) => depth.set(id, 0));
  while (queue.length > 0) {
    const id = queue.shift()!;
    for (const f of flows.filter((x) => x.source === id)) {
      if (!depth.has(f.target)) {
        depth.set(f.target, (depth.get(id) ?? 0) + 1);
        queue.push(f.target);
      }
    }
  }
  const rows = new Map<number, number>();
  for (const e of elements) {
    if (placed.has(e.id)) continue;
    const column = e.type === "boundaryEvent" ? (depth.get(e.attachedTo) ?? 0) : (depth.get(e.id) ?? 0);
    const row = rows.get(column) ?? 0;
    rows.set(column, row + 1);
    e.x = 80 + column * 180;
    e.y = 80 + row * 140 + (e.type === "boundaryEvent" ? 60 : 0);
  }
}

const slug = (value: string) => value.replace(/[^A-Za-z0-9_]/g, "_").replace(/^([^A-Za-z])/, "x$1").slice(0, 60) || "x";

/** The draft as BPMN 2.0 XML with TeamBeats extension elements and diagram interchange for every shape and flow. */
export function writeProcess(draft: ProcessDraft): string {
  const doc = document.implementation.createDocument(BPMN_NS, "bpmn:definitions", null);
  const root = doc.documentElement;
  root.setAttributeNS(XMLNS, "xmlns:bpmn", BPMN_NS);
  root.setAttributeNS(XMLNS, "xmlns:bpmndi", BPMNDI_NS);
  root.setAttributeNS(XMLNS, "xmlns:dc", DC_NS);
  root.setAttributeNS(XMLNS, "xmlns:di", DI_NS);
  root.setAttributeNS(XMLNS, "xmlns:tb", TB_NS);
  root.setAttribute("id", "definitions");
  root.setAttribute("targetNamespace", "https://teambeats.example/processes");
  const el = (parent: Element, name: string, attrs: Record<string, string> = {}, ns = BPMN_NS, prefix = "bpmn") => {
    const e = doc.createElementNS(ns, `${prefix}:${name}`);
    for (const [k, v] of Object.entries(attrs)) if (v !== "") e.setAttribute(k, v);
    parent.appendChild(e);
    return e;
  };
  const tb = (parent: Element, name: string, attrs: Record<string, string> = {}) => el(parent, name, attrs, TB_NS, "tb");

  const messageIds = new Map<string, string>();
  const errorIds = new Map<string, string>();
  const escalationIds = new Map<string, string>();
  for (const e of draft.elements) {
    const usesMessage = e.event === "message" || e.type === "sendTask" || e.type === "receiveTask";
    if (usesMessage && e.message.trim() && !messageIds.has(e.message.trim())) messageIds.set(e.message.trim(), `message_${slug(e.message.trim())}`);
    if (e.event === "error" && e.code.trim() && !errorIds.has(e.code.trim())) errorIds.set(e.code.trim(), `error_${slug(e.code.trim())}`);
    if (e.event === "escalation" && e.code.trim() && !escalationIds.has(e.code.trim())) escalationIds.set(e.code.trim(), `escalation_${slug(e.code.trim())}`);
  }
  messageIds.forEach((id, name) => el(root, "message", { id, name }));
  errorIds.forEach((id, code) => el(root, "error", { id, errorCode: code }));
  escalationIds.forEach((id, code) => el(root, "escalation", { id, escalationCode: code }));

  const process = el(root, "process", { id: draft.processId, name: draft.nameEn.trim(), isExecutable: "true" });
  if (draft.nameAr.trim()) {
    const x = el(process, "extensionElements");
    const n = tb(x, "name");
    n.setAttributeNS(XML_NS, "xml:lang", "ar");
    n.textContent = draft.nameAr.trim();
  }

  for (const e of draft.elements) {
    const attrs: Record<string, string> = { id: e.id, name: e.nameEn.trim() };
    if (e.type === "boundaryEvent") {
      attrs.attachedToRef = e.attachedTo;
      if (!e.interrupting) attrs.cancelActivity = "false";
    }
    if (e.type === "startEvent" && !e.interrupting) attrs.isInterrupting = "false";
    if ((e.type === "exclusiveGateway" || e.type === "inclusiveGateway") && e.defaultFlow) attrs.default = e.defaultFlow;
    if ((e.type === "sendTask" || e.type === "receiveTask") && e.message.trim()) attrs.messageRef = messageIds.get(e.message.trim()) ?? "";
    if (e.type === "callActivity") attrs.calledElement = e.calledKey.trim();
    const node = el(process, e.type, attrs);

    const extensions: [string, Record<string, string>, string?][] = [];
    if (e.nameAr.trim()) extensions.push(["name", {}, e.nameAr.trim()]);
    if (e.type === "userTask") {
      extensions.push([
        "assignment",
        {
          users: e.users.trim(),
          permission: e.permission.trim(),
          lineManager: e.lineManager ? "true" : "",
          managerLevel: e.managerLevel.trim(),
          assignee: e.assignee.trim(),
          fallbackUsers: e.fallbackUsers.trim(),
        },
      ]);
      if (e.dueWorkingDays.trim()) extensions.push(["due", { workingDays: e.dueWorkingDays.trim() }]);
    }
    if (e.type === "serviceTask") {
      if (e.serviceKind === "webhook") extensions.push(["webhook", { id: e.webhook.trim(), retries: e.retries.trim() }]);
      else extensions.push(["connector", { connection: e.connection.trim(), action: e.action.trim(), retries: e.retries.trim() }]);
    }
    if (e.type === "serviceTask" || e.type === "callActivity" || e.type === "sendTask") {
      for (const m of e.inputs.filter((x) => x.key.trim())) extensions.push(["input", { name: m.key.trim(), source: m.source.trim() }]);
    }
    if (e.type === "serviceTask" || e.type === "callActivity" || e.type === "userTask") {
      for (const m of e.outputs.filter((x) => x.key.trim())) extensions.push(["output", { variable: m.key.trim(), source: m.source.trim() }]);
    }
    if (e.type === "businessRuleTask") extensions.push(["decision", { key: e.decisionKey.trim(), resultVariable: e.resultVariable.trim() }]);
    const correlates = e.event === "message" || e.type === "sendTask" || e.type === "receiveTask";
    if (correlates && e.correlationKey.trim()) extensions.push(["correlation", { key: e.correlationKey.trim() }]);
    if (e.event === "timer" && e.timerKind === "workingDays") extensions.push(["timer", { workingDays: e.timerValue.trim() }]);
    if (e.loop) extensions.push(["loop", { collection: e.loopCollection.trim(), elementVariable: e.loopVariable.trim() }]);
    if (extensions.length > 0) {
      const x = el(node, "extensionElements");
      for (const [name, a, text] of extensions) {
        const t = tb(x, name, a);
        if (name === "name") {
          t.setAttributeNS(XML_NS, "xml:lang", "ar");
          t.textContent = text ?? "";
        }
      }
    }

    switch (e.event) {
      case "timer": {
        const def = el(node, "timerEventDefinition");
        if (e.timerKind !== "workingDays") {
          const name = e.timerKind === "date" ? "timeDate" : e.timerKind === "cycle" ? "timeCycle" : "timeDuration";
          el(def, name).textContent = e.timerValue.trim();
        }
        break;
      }
      case "message":
        if (EVENTS_FOR[e.type]) el(node, "messageEventDefinition", { messageRef: messageIds.get(e.message.trim()) ?? "" });
        break;
      case "error":
        el(node, "errorEventDefinition", { errorRef: errorIds.get(e.code.trim()) ?? "" });
        break;
      case "escalation":
        el(node, "escalationEventDefinition", { escalationRef: escalationIds.get(e.code.trim()) ?? "" });
        break;
      case "terminate":
        el(node, "terminateEventDefinition");
        break;
    }
    if (e.loop) {
      const loop = el(node, "multiInstanceLoopCharacteristics", { isSequential: e.loopSequential ? "true" : "" });
      if (e.loopCompletion.trim()) el(loop, "completionCondition").textContent = e.loopCompletion.trim();
    }
  }
  for (const f of draft.flows) {
    const flow = el(process, "sequenceFlow", { id: f.id, sourceRef: f.source, targetRef: f.target });
    if (f.condition.trim()) el(flow, "conditionExpression").textContent = f.condition.trim();
  }

  const diagram = el(root, "BPMNDiagram", { id: "diagram" }, BPMNDI_NS, "bpmndi");
  const plane = el(diagram, "BPMNPlane", { id: "plane", bpmnElement: draft.processId }, BPMNDI_NS, "bpmndi");
  const centre = new Map<string, { x: number; y: number }>();
  for (const e of draft.elements) {
    const { width, height } = size(e.type);
    const shape = el(plane, "BPMNShape", { id: `${e.id}_di`, bpmnElement: e.id }, BPMNDI_NS, "bpmndi");
    el(shape, "Bounds", { x: String(Math.round(e.x)), y: String(Math.round(e.y)), width: String(width), height: String(height) }, DC_NS, "dc");
    centre.set(e.id, { x: Math.round(e.x + width / 2), y: Math.round(e.y + height / 2) });
  }
  for (const f of draft.flows) {
    const edge = el(plane, "BPMNEdge", { id: `${f.id}_di`, bpmnElement: f.id }, BPMNDI_NS, "bpmndi");
    for (const point of [centre.get(f.source), centre.get(f.target)]) {
      if (point) el(edge, "waypoint", { x: String(point.x), y: String(point.y) }, DI_NS, "di");
    }
  }
  return `<?xml version="1.0" encoding="UTF-8"?>\n${new XMLSerializer().serializeToString(doc)}`;
}
