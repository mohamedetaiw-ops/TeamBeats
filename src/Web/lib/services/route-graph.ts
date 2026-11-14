import type { ActionStep, ApprovalStep, Names } from "./types";

/**
 * The step graph of a service route (spec 016 R2, US5 and US8) as the API stores it. The designer edits this shape
 * and the server checks every rule when the draft is saved; nothing here decides what a valid route is.
 */
export const STEP_TYPES = [
  "approval",
  "task",
  "condition",
  "split",
  "join",
  "set_field",
  "notify",
  "connector",
  "webhook",
  "wait",
  "start_service",
  "end",
] as const;
export type StepType = (typeof STEP_TYPES)[number];

export const ASSIGNEE_KINDS = ["users", "permission_at_unit", "line_manager", "manager_level", "group"] as const;
export type AssigneeKind = (typeof ASSIGNEE_KINDS)[number];

export const OPERATORS = ["eq", "ne", "gt", "ge", "lt", "le"] as const;
export type Operator = (typeof OPERATORS)[number];

export const END_RESULTS = ["completed", "approved", "rejected"] as const;
export const STEP_OUTCOMES = ["approve", "reject", "return"] as const;

export type StepCondition = {
  field: string;
  operator: Operator;
  value: string | number | boolean;
};
export type StepAssignee = {
  kind: AssigneeKind;
  users?: string[] | null;
  permission?: string | null;
  level?: number | null;
  groups?: string[] | null;
};
export type StepEscalation = {
  afterWorkingDays: number;
  users?: string[] | null;
  groups?: string[] | null;
};
export type NotifyTarget = {
  requester: boolean;
  subject: boolean;
  users?: string[] | null;
  groups?: string[] | null;
};

export type RouteNode = {
  id: string;
  type: StepType;
  names?: Names | null;
  assignee?: StepAssignee | null;
  dueWorkingDays?: number | null;
  escalation?: StepEscalation | null;
  connectionId?: string | null;
  action?: string | null;
  webhookId?: string | null;
  inputs?: Record<string, string> | null;
  field?: string | null;
  value?: string | null;
  notify?: NotifyTarget | null;
  waitWorkingDays?: number | null;
  serviceId?: string | null;
  result?: string | null;
  autoApprove?: StepCondition[] | null;
};
export type RouteLink = {
  from: string;
  to: string;
  when?: StepCondition[] | null;
};
export type RouteGraph = { steps: RouteNode[]; links: RouteLink[] };

export const TRIGGER_EVENTS = ["user_provisioned", "user_deprovisioned", "membership_changed", "request_completed"] as const;
export const FREQUENCIES = ["weekly", "monthly", "quarterly"] as const;
export type ServiceTrigger = {
  kind: "schedule" | "event";
  event?: string | null;
  serviceId?: string | null;
  status?: string | null;
  orgUnitId?: string | null;
  frequency?: string | null;
  day?: number | null;
  requesterId?: string | null;
};

export type RouteGroup = { id: string; name: string; members: number };

export type RouteTestPerson = { id: string; name: string };
export type RouteTestStep = {
  stepId: string;
  names: Names | null;
  event: string;
  assignees: RouteTestPerson[] | null;
  permission: string | null;
  lineManager: boolean;
};
export type RouteTestProblem = {
  field: string;
  stepId: string;
  key: string;
  message: string;
};
export type RouteTestResult = {
  outcome: string;
  steps: RouteTestStep[];
  problems: RouteTestProblem[];
};

export const isHuman = (type: StepType) => type === "approval" || type === "task";
/** Steps whose paths may carry conditions. */
export const branches = (type: StepType) => type === "condition" || type === "split";

/** A step ID the API accepts: lowercase letters, digits and underscores, starting with a letter, unique in the graph. */
export function newStepId(type: StepType, graph: RouteGraph): string {
  const used = new Set(graph.steps.map((s) => s.id));
  for (let n = 1; ; n++) {
    const id = `${type}_${n}`;
    if (!used.has(id)) return id;
  }
}

/** A new step with the settings its type needs left for the owner to fill in. */
export function blankStep(type: StepType, id: string): RouteNode {
  switch (type) {
    case "approval":
    case "task":
      return { id, type, assignee: { kind: "line_manager" } };
    case "notify":
      return { id, type, notify: { requester: true, subject: false } };
    case "wait":
      return { id, type, waitWorkingDays: 1 };
    case "set_field":
      return { id, type, field: "", value: "" };
    case "end":
      return { id, type, result: "completed" };
    default:
      return { id, type };
  }
}

/** The R1 route (one approval, then connector steps in order) as a graph the designer can open, ending as completed. */
export function graphFromR1(approval: ApprovalStep | null, actions: ActionStep[], queueGroupId?: string | null): RouteGraph {
  const steps: RouteNode[] = [];
  if (approval) {
    const a = approval.approvers;
    steps.push({
      id: "approval_1",
      type: "approval",
      names: approval.names,
      dueWorkingDays: approval.dueWorkingDays ?? null,
      assignee:
        a.kind === "users"
          ? { kind: "users", users: a.users ?? [] }
          : a.kind === "line_manager"
            ? { kind: "line_manager", permission: a.permission ?? null }
            : { kind: "permission_at_unit", permission: a.permission ?? null },
    });
  }
  actions.forEach((step, i) =>
    steps.push({
      id: `connector_${i + 1}`,
      type: "connector",
      names: step.names,
      connectionId: step.connectionId,
      action: step.action,
      inputs: step.inputs,
    }),
  );
  // The fulfilment queue picks up the work after the automatic steps.
  if (queueGroupId)
    steps.push({
      id: "task_1",
      type: "task",
      assignee: { kind: "group", groups: [queueGroupId] },
    });
  steps.push({ id: "end_1", type: "end", result: "completed" });
  const links = steps.slice(1).map((s, i) => ({ from: steps[i]!.id, to: s.id }));
  return { steps, links };
}

/** The first step: the one no path leads into (the first listed when there is none or several). */
export function firstStep(graph: RouteGraph): RouteNode | undefined {
  return graph.steps.find((s) => !graph.links.some((l) => l.to === s.id)) ?? graph.steps[0];
}

/**
 * The steps in reading order, breadth first from the first step, then any the paths do not reach. The outline and the
 * canvas layout both use it, so a screen reader hears the route in the order it is drawn.
 */
export function readingOrder(graph: RouteGraph): { id: string; depth: number }[] {
  const order: { id: string; depth: number }[] = [];
  const seen = new Set<string>();
  const first = firstStep(graph);
  const queue = first ? [{ id: first.id, depth: 0 }] : [];
  while (queue.length > 0) {
    const item = queue.shift()!;
    if (seen.has(item.id)) continue;
    seen.add(item.id);
    order.push(item);
    for (const l of graph.links.filter((x) => x.from === item.id)) {
      if (!seen.has(l.to)) queue.push({ id: l.to, depth: item.depth + 1 });
    }
  }
  const deepest = order.reduce((max, o) => Math.max(max, o.depth), -1);
  graph.steps.filter((s) => !seen.has(s.id)).forEach((s, i) => order.push({ id: s.id, depth: deepest + 1 + i }));
  return order;
}

/** Canvas positions by depth: columns from the first step, rows within a column. Positions are not stored. */
export function layout(graph: RouteGraph, column = 220, row = 110): Map<string, { x: number; y: number }> {
  const rows = new Map<number, number>();
  const positions = new Map<string, { x: number; y: number }>();
  for (const { id, depth } of readingOrder(graph)) {
    const r = rows.get(depth) ?? 0;
    rows.set(depth, r + 1);
    positions.set(id, { x: depth * column, y: r * row });
  }
  return positions;
}

/**
 * Problems from the API by step: validation errors keyed route.graph.steps[i] (and links[i], which belong to their
 * source step), and engine problems that name the step. Problems on the whole graph come back under "".
 */
export function problemsByStep(graph: RouteGraph, errors: Record<string, { key: string; message: string }[]>): Map<string, string[]> {
  const out = new Map<string, string[]>();
  const add = (id: string, messages: string[]) => out.set(id, [...(out.get(id) ?? []), ...messages]);
  for (const [field, list] of Object.entries(errors)) {
    const messages = list.map((e) => e.message || e.key);
    const step = /^route\.graph\.steps\[(\d+)\]/.exec(field);
    const link = /^route\.graph\.links\[(\d+)\]/.exec(field);
    if (step) add(graph.steps[Number(step[1])]?.id ?? "", messages);
    else if (link) add(graph.links[Number(link[1])]?.from ?? "", messages);
    else if (field.startsWith("route")) add("", messages);
  }
  return out;
}

/** The graph as the API takes it: empty settings dropped and the names trimmed to the languages written. */
export function cleanGraph(graph: RouteGraph, clean: (n: Names | null | undefined) => Names | null): RouteGraph {
  const ids = (list: string[] | null | undefined) => (list && list.length > 0 ? list : null);
  return {
    steps: graph.steps.map((s) => ({
      ...s,
      names: clean(s.names),
      inputs: s.inputs && Object.keys(s.inputs).length > 0 ? s.inputs : null,
      autoApprove: s.type === "approval" && s.autoApprove && s.autoApprove.length > 0 ? s.autoApprove : null,
      escalation: s.escalation
        ? {
            ...s.escalation,
            users: ids(s.escalation.users),
            groups: ids(s.escalation.groups),
          }
        : null,
      notify: s.notify
        ? {
            ...s.notify,
            users: ids(s.notify.users),
            groups: ids(s.notify.groups),
          }
        : null,
    })),
    links: graph.links.map((l) => ({
      ...l,
      when: l.when && l.when.length > 0 ? l.when : null,
    })),
  };
}
