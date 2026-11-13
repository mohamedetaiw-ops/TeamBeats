/**
 * Reads and writes the one decision table of a DMN 1.3 file (spec 024 FR-007) with the browser's own XML parser, so
 * the decision table editor works on plain values. The server reads the same XML again and decides whether it is
 * valid; nothing here judges a table.
 */
export const DMN_NS = "https://www.omg.org/spec/DMN/20191111/MODEL/";
export const TB_NS = "https://teambeats.example/schema/bpmn/1.0";
const XML_NS = "http://www.w3.org/XML/1998/namespace";
const DMN_NAMESPACES = [DMN_NS, "http://www.omg.org/spec/DMN/20180521/MODEL/", "https://www.omg.org/spec/DMN/20230324/MODEL/"];

export const HIT_POLICIES = ["UNIQUE", "FIRST", "PRIORITY", "ANY", "COLLECT", "RULE ORDER", "OUTPUT ORDER"] as const;
export type HitPolicy = (typeof HIT_POLICIES)[number];
export const AGGREGATIONS = ["", "SUM", "MIN", "MAX", "COUNT"] as const;
export type Aggregation = (typeof AGGREGATIONS)[number];

export type DecisionInputDraft = { id: string; label: string; expression: string };
export type DecisionOutputDraft = { id: string; name: string; values: string };
export type DecisionRuleDraft = { id: string; inputs: string[]; outputs: string[]; description: string };

export type DecisionDraft = {
  names: { en: string; ar: string };
  hitPolicy: HitPolicy;
  aggregation: Aggregation;
  inputs: DecisionInputDraft[];
  outputs: DecisionOutputDraft[];
  rules: DecisionRuleDraft[];
};

let counter = 0;
/** A new element ID that starts with a letter, as DMN and the server expect. */
export function newId(prefix: string): string {
  counter += 1;
  return `${prefix}_${Date.now().toString(36)}${counter.toString(36)}`;
}

export function blankDecision(): DecisionDraft {
  const input = { id: newId("input"), label: "", expression: "" };
  const output = { id: newId("output"), name: "", values: "" };
  return {
    names: { en: "", ar: "" },
    hitPolicy: "UNIQUE",
    aggregation: "",
    inputs: [input],
    outputs: [output],
    rules: [{ id: newId("rule"), inputs: ["-"], outputs: [""], description: "" }],
  };
}

function children(parent: Element, name: string): Element[] {
  return Array.from(parent.children).filter((c) => c.localName === name && DMN_NAMESPACES.includes(c.namespaceURI ?? ""));
}

function text(parent: Element, name: string): string {
  const el = children(parent, name)[0];
  return el ? (children(el, "text")[0]?.textContent?.trim() ?? "") : "";
}

/** The draft in a DMN file, or null when the file has no single decision table this editor can show. */
export function readDecision(xml: string): DecisionDraft | null {
  if (!xml.trim()) return null;
  const doc = new DOMParser().parseFromString(xml, "application/xml");
  if (doc.getElementsByTagName("parsererror").length > 0) return null;
  const root = doc.documentElement;
  const decision = children(root, "decision")[0];
  const table = decision ? children(decision, "decisionTable")[0] : undefined;
  if (!decision || !table) return null;
  const ar = Array.from(decision.getElementsByTagNameNS(TB_NS, "name")).find((n) => n.getAttributeNS(XML_NS, "lang") === "ar")?.textContent?.trim() ?? "";
  const policy = (table.getAttribute("hitPolicy") ?? "UNIQUE").trim() as HitPolicy;
  const aggregation = (table.getAttribute("aggregation") ?? "").trim() as Aggregation;
  return {
    names: { en: decision.getAttribute("name") ?? "", ar },
    hitPolicy: HIT_POLICIES.includes(policy) ? policy : "UNIQUE",
    aggregation: AGGREGATIONS.includes(aggregation) ? aggregation : "",
    inputs: children(table, "input").map((i, n) => ({ id: i.getAttribute("id") ?? `input${n + 1}`, label: i.getAttribute("label") ?? "", expression: text(i, "inputExpression") })),
    outputs: children(table, "output").map((o, n) => ({ id: o.getAttribute("id") ?? `output${n + 1}`, name: o.getAttribute("name") ?? "", values: text(o, "outputValues") })),
    rules: children(table, "rule").map((r, n) => ({
      id: r.getAttribute("id") ?? `rule${n + 1}`,
      inputs: children(r, "inputEntry").map((e) => children(e, "text")[0]?.textContent?.trim() || "-"),
      outputs: children(r, "outputEntry").map((e) => children(e, "text")[0]?.textContent?.trim() ?? ""),
      description: children(r, "description")[0]?.textContent?.trim() ?? "",
    })),
  };
}

/** The draft as a DMN 1.3 file with one decision whose ID is the key; names per locale go in TeamBeats extension elements. */
export function writeDecision(key: string, draft: DecisionDraft): string {
  const doc = document.implementation.createDocument(DMN_NS, "definitions", null);
  const root = doc.documentElement;
  root.setAttributeNS("http://www.w3.org/2000/xmlns/", "xmlns:tb", TB_NS);
  root.setAttribute("id", `${key.replace(/[^A-Za-z0-9_-]/g, "_")}_definitions`);
  root.setAttribute("name", draft.names.en || key);
  root.setAttribute("namespace", "https://teambeats.example/decisions");
  const el = (parent: Element, name: string, attrs: Record<string, string> = {}) => {
    const e = doc.createElementNS(DMN_NS, name);
    for (const [k, v] of Object.entries(attrs)) if (v !== "") e.setAttribute(k, v);
    parent.appendChild(e);
    return e;
  };
  const withText = (parent: Element, name: string, value: string) => {
    const wrapper = el(parent, name);
    el(wrapper, "text").textContent = value;
    return wrapper;
  };
  const decision = el(root, "decision", { id: "decision", name: draft.names.en.trim() });
  if (draft.names.ar.trim()) {
    const ext = el(decision, "extensionElements");
    const ar = doc.createElementNS(TB_NS, "tb:name");
    ar.setAttributeNS(XML_NS, "xml:lang", "ar");
    ar.textContent = draft.names.ar.trim();
    ext.appendChild(ar);
  }
  const table = el(decision, "decisionTable", { id: "table", hitPolicy: draft.hitPolicy, aggregation: draft.hitPolicy === "COLLECT" ? draft.aggregation : "" });
  for (const input of draft.inputs) {
    const i = el(table, "input", { id: input.id, label: input.label.trim() });
    const expression = el(i, "inputExpression", { id: `${input.id}_expression` });
    el(expression, "text").textContent = input.expression.trim();
  }
  for (const output of draft.outputs) {
    const o = el(table, "output", { id: output.id, name: output.name.trim() });
    if (output.values.trim()) withText(o, "outputValues", output.values.trim());
  }
  for (const rule of draft.rules) {
    const r = el(table, "rule", { id: rule.id });
    if (rule.description.trim()) el(r, "description").textContent = rule.description.trim();
    rule.inputs.forEach((cell, n) => {
      const entry = el(r, "inputEntry", { id: `${rule.id}_in${n + 1}` });
      el(entry, "text").textContent = cell.trim() || "-";
    });
    rule.outputs.forEach((cell, n) => {
      const entry = el(r, "outputEntry", { id: `${rule.id}_out${n + 1}` });
      el(entry, "text").textContent = cell.trim();
    });
  }
  return `<?xml version="1.0" encoding="UTF-8"?>\n${new XMLSerializer().serializeToString(doc)}`;
}
