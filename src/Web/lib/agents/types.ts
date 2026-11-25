/** Shapes of the agent API (specs 020 and 027). The API decides every rule; these types only carry what it returns. */

export type Localized = Record<string, string>;

export type AgentAudience = { scope: "tenant" | "org_units" | null; orgUnits: string[] | null; outside?: string | null };

export type AgentDefinition = {
  name: Localized | null;
  description: Localized | null;
  icon: string | null;
  category: string | null;
  instructions: Localized | null;
  tone: string | null;
  outputFormat: string | null;
  knowledge: string[] | null;
  tools: string[] | null;
  audience: AgentAudience | null;
  languages: string[] | null;
  escalation: Localized | null;
  autonomy: string | null;
  maxClassification: string | null;
  maxToolCalls: number | null;
  purpose?: Localized | null;
  memory?: { mode: string | null; retentionDays?: number | null } | null;
  briefing?: { enabled: boolean; prompt: Localized | null } | null;
  /** Declared handoffs (spec 020 US10): a platform template target or an agent ID, and the topic per language. */
  handoffs?: AgentHandoff[] | null;
};

export type AgentHandoff = { template?: string | null; agent?: string | null; topic: Localized | null };

/** The most handoffs one agent declares (spec 020 FR-016). */
export const MAX_HANDOFFS = 5;

/** A daily briefing offer and the person's subscription (spec 020 US8). Days are ISO weekdays, 1 for Monday. */
export type BriefingView = {
  agentId: string;
  offered: boolean;
  defaultTimeZone: string;
  subscription: { time: string; days: number[]; timeZone: string; nextRunAt: string; lastRunAt: string | null } | null;
};

export const MEMORY_MODES = ["session", "persistent"] as const;

export type MemoryItem = { id: string; agentId: string; text: string; classification: string; createdAt: string; updatedAt: string; lastUsedAt: string };

/** One agent's memory as the signed-in person sees it (spec 020 US9). The API decides whether it is offered. */
export type MyMemoryView = { agents: { agentId: string; name: Localized | null; accepted: boolean; items: MemoryItem[] }[] };

export type AgentMemoryView = { agentId: string; offered: boolean; accepted: boolean; retentionDays: number; items: MemoryItem[] };

/** Agent categories of the definition schema (spec 020 section 12, Identity). */
export const AGENT_CATEGORIES = ["knowledge", "hr", "it", "content", "requests", "productivity", "people", "learning", "procurement", "finance", "communications", "other"] as const;

/** What Agent Studio offers the designer (spec 020 US4): blank agents, the custom agent count and limit, and the JSON view. */
export type StudioOffer = { custom: boolean; customCount: number; customLimit: number | null; jsonView: boolean };

export type AgentStatus = "draft" | "in_review" | "published" | "suspended" | "retired";

export type AgentSummary = {
  id: string;
  name: Localized | null;
  description: Localized | null;
  icon: string | null;
  category: string | null;
  status: AgentStatus;
  riskClass: "low" | "medium" | "high";
  orgUnitId: string;
  ownerUserId: string;
  templateId: string | null;
  templateVersion: number | null;
  currentVersion: number | null;
  latestVersion: number;
  updatedAt: string;
  backupOwnerUserId: string | null;
  ownerFlaggedAt: string | null;
};

export type AgentVersion = {
  number: number;
  state: "draft" | "in_review" | "published" | "rejected";
  riskClass: string;
  revision: number;
  lastEditor: string;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
};

export type AgentDetail = {
  agent: AgentSummary;
  definition: AgentDefinition;
  latest: AgentVersion;
  versions: AgentVersion[];
  canEdit: boolean;
  templateUpdate: number | null;
  /** Tools the agent holds that cannot be called now, such as those of a disabled connection (spec 027 US7 scenario 3). */
  unavailableTools?: string[] | null;
  /** Forced security updates of the agent's template (spec 020 FR-005): in force ones already apply, the others on their date. */
  securityUpdates?: TemplateSecurityUpdate[] | null;
};

export type TemplateSecurityUpdate = {
  version: number;
  reason: Localized | null;
  appliesAt: string;
  critical: boolean;
  inForce: boolean;
  removeTools: string[] | null;
  maxAutonomy: string | null;
  maxClassification: string | null;
  maxToolCalls: number | null;
};

export type TemplateView = {
  id: string;
  version: number;
  name: Localized | null;
  description: Localized | null;
  icon: string | null;
  category: string | null;
  autonomy: string | null;
  tools: string[] | null;
  ready: boolean;
  missingTools: string[];
  blockedTools: string[];
  autonomyAbovePolicy: boolean;
  /** An L4 template whose package does not include bounded autonomy (spec 020 section 12). */
  needsAutonomy?: boolean;
};

/** A tenant tool (a connector action, spec 017) carries the title its administrator entered per language. */
export type ToolView = {
  name: string;
  module: string;
  sideEffect: string;
  defaultRule: string;
  rule: string;
  allowed: boolean;
  title?: Record<string, string> | null;
};

/** The title a tenant entered for its own tool, in the reader's language or the first one given. */
export function tenantToolTitle(tools: ToolView[], name: string, locale: string): string | null {
  const title = tools.find((x) => x.name === name)?.title;
  if (!title) return null;
  return title[locale] ?? Object.values(title)[0] ?? null;
}

export type FieldChange = { field: string; from: unknown; to: unknown };

export type AgentReview = {
  agent: AgentSummary;
  version: AgentVersion;
  definition: AgentDefinition;
  publishedVersion: number | null;
  changes: FieldChange[];
  gate: AgentGate;
  canApprove: boolean;
  cannotApproveReason: string | null;
};

/** The release gate of a version (spec 034 US5): status, failures by severity, failing cases and the score diff. */
export type AgentGate = {
  status: string;
  blocking: boolean;
  failingCases: string[];
  runId?: string | null;
  canOverride?: boolean;
  criticalFailures?: string[] | null;
  blockingFailures?: string[] | null;
  incomplete?: string[] | null;
  diff?: { metric: string; locale: string; published: number | null; value: number | null }[] | null;
  overrideReason?: string | null;
};

export type AgentPolicy = {
  maxAutonomy: string;
  maxClassification: string;
  allowedTools: string[] | null;
  toolRules: Record<string, string> | null;
  scheduledAgents: boolean;
  scheduledRiskClasses?: string[] | null;
  persistentMemory: boolean;
  maxToolCalls: number;
  maxSteps: number;
  maxRunSeconds: number;
  maxDailyRunsPerAgent: number;
  external?: ExternalAiPolicy | null;
  contextualActions?: Record<string, ContextualActionRule> | null;
};

/** Where "Ask an agent" shows for one kind of item, and which agents it offers there; no list means any agent (spec 020 FR-012). */
export type ContextualActionRule = { enabled: boolean; agents?: string[] | null };

/** The kinds of item a contextual action can start from, as Search names them. */
export const CONTEXTUAL_KINDS = ["content", "knowledge", "policy", "document"] as const;

/** Which external AI clients may reach TeamBeats through MCP, each with the highest classification it may receive (spec 035 FR-020). */
export type ExternalAiPolicy = {
  enabled: boolean;
  clients: { clientId: string; maxClassification: string }[] | null;
  allowedTools: string[] | null;
  writeTools: boolean;
  /** Verified AI clients that can ask the employee may confirm write actions inside the client (spec 035 R3). Off by default. */
  confirmInClient?: boolean;
};

/** The classifications an external AI client may be capped at; Restricted never leaves TeamBeats. */
/** A client on the platform's verified MCP client list that the tenant may allow (spec 035 FR-023). */
export type VerifiedAiClient = { clientId: string; name: string; publisher: string; scopes: string[] };

export const EXTERNAL_CLASSIFICATIONS = ["public", "internal", "confidential"] as const;

export type AgentPolicyView = { version: number; policy: AgentPolicy; updatedAt: string | null };

export type KillSwitchView = { active: boolean; reason: string | null; changedAt: string | null };

export type RunCitation = { n: number; source: string; itemId: string; title: string; link: string };

export type ConfirmationCard = {
  id: string;
  tool: string;
  sideEffect: string;
  parameters: Record<string, unknown>;
  hash: string;
  expiresAt: string;
  fromContent: string[];
  awaitingApproval: boolean;
};

export type RunView = {
  id: string;
  agentId: string;
  agentVersion: number;
  mode: "run" | "test";
  status: "running" | "waiting" | "completed" | "stopped" | "failed";
  outcome: string | null;
  reason: string | null;
  answer: string | null;
  language: string;
  citations: RunCitation[];
  confirmation: ConfirmationCard | null;
  generated: boolean;
  /** The answer names web addresses; they are shown as text only (spec 027 US5 scenario 3). */
  linksAsText: boolean;
  /** Another agent took over through a declared handoff (spec 020 US10): the agent asked and the agent that answered. */
  handedOffFrom?: { agentId: string; runId: string; agentNames: Localized | null; toNames: Localized | null } | null;
};

/** What a streamed run sends (spec 020 T021): its ID, each tool step, answer text as it arrives, then the checked answer. */
export type RunStreamHandlers = {
  onRun: (id: string) => void;
  onStep: (step: { kind: string; tool: string | null; decision: string }) => void;
  onDelta: (text: string) => void;
};

/** Reads a streamed run. Returns the finished run, or null when the stream ended without one (the run carries on). */
export async function readRunStream(response: Response, handlers: RunStreamHandlers): Promise<RunView | null> {
  const reader = response.body?.getReader();
  if (!reader) return null;
  const decoder = new TextDecoder();
  let buffer = "";
  let done: RunView | null = null;
  for (;;) {
    const { value, done: end } = await reader.read();
    if (value) buffer += decoder.decode(value, { stream: true });
    let cut = buffer.indexOf("\n\n");
    while (cut >= 0) {
      const block = buffer.slice(0, cut);
      buffer = buffer.slice(cut + 2);
      const event = /^event: (.+)$/m.exec(block)?.[1];
      const data = /^data: (.+)$/m.exec(block)?.[1];
      if (event && data) {
        const payload = JSON.parse(data) as Record<string, unknown>;
        if (event === "run") handlers.onRun(String(payload.id));
        if (event === "step") handlers.onStep(payload as { kind: string; tool: string | null; decision: string });
        if (event === "delta" && typeof payload.text === "string") handlers.onDelta(payload.text);
        if (event === "done") done = payload as unknown as RunView;
      }
      cut = buffer.indexOf("\n\n");
    }
    if (end) break;
  }
  return done;
}

export type RunSummary = {
  id: string;
  agentId: string;
  agentVersion: number;
  userId: string;
  mode: string;
  status: string;
  outcome: string | null;
  reason: string | null;
  steps: number;
  toolCalls: number;
  denials: number;
  model: string | null;
  createdAt: string;
  finishedAt: string | null;
};

export type RunStep = {
  n: number;
  kind: "model" | "tool" | "confirmation";
  tool: string | null;
  decision: string;
  reason: string | null;
  parameterHash: string | null;
  parameters: unknown;
  classification: string | null;
  durationMs: number;
  at: string;
};

export type RunRecord = {
  run: RunSummary;
  steps: RunStep[];
  sources: RunCitation[];
  route: { id: string; method: string; candidates: number; agentId: string | null; model: string | null; at: string } | null;
  itemSource: string | null;
  itemId: string | null;
};

export type AgentChoice = { id: string; name: Localized | null; description: Localized | null; icon: string | null; category: string | null; favourite?: boolean; degraded?: boolean };

/** An agent in the marketplace as the signed-in person sees it (spec 020 US6). The API decides every flag. */
export type MarketplaceAgent = {
  id: string;
  name: Localized | null;
  description: Localized | null;
  icon: string | null;
  category: string | null;
  departmentId: string;
  department: Localized | null;
  canUse: boolean;
  request: "pending" | "granted" | "denied" | null;
  favourite: boolean;
  rating: number | null;
  ratings: number;
  myScore: number | null;
};

export type MarketplaceView = { paused: boolean; agents: MarketplaceAgent[] };

export type AccessRequestView = { id: string; agentId: string; userId: string; status: "pending" | "granted" | "denied"; createdAt: string; userName: string | null };

/** Who outside an agent's audience sees it in the marketplace (spec 020 FR-010). */
export const AUDIENCE_OUTSIDE = ["hidden", "request_access"] as const;

export const AUTONOMY_BUILT = ["L0", "L1", "L2", "L3"] as const;
export const TONES = ["neutral", "friendly", "formal"] as const;
export const OUTPUT_FORMATS = ["text", "bullets", "steps"] as const;
export const CLASSIFICATIONS = ["public", "internal", "confidential", "restricted"] as const;

/** The text for a locale, falling back to English, then to any language the item has. */
export function pickText(value: Localized | null | undefined, locale: string): string {
  if (!value) return "";
  return value[locale] ?? value.en ?? Object.values(value)[0] ?? "";
}

export type AgentVersionStats = {
  version: number | null;
  runs: number;
  users: number;
  answered: number;
  successRate: number | null;
  stopped: number;
  failed: number;
  toolCalls: number;
  toolDenials: number;
  ratings: number;
  helpful: number;
  tokens: number;
  cost: number;
  quality: string | null;
};

export type AgentQualityScore = { metric: string; locale: string; value: number | null };
export type AgentVersionDetail = { version: number | null; conversations: number; escalations: number; resolvedRate: number | null; quality: AgentQualityScore[] };
export type AgentToolStats = {
  tool: string;
  calls: number;
  succeeded: number;
  failed: number;
  successRate: number | null;
  averageMs: number;
  failures: { reason: string; calls: number }[];
};
export type AgentDayStats = { day: string; runs: number; answered: number; escalations: number; ratings: number; helpful: number };
export type AgentAnalyticsDetail = {
  versions: AgentVersionDetail[];
  outcomes: { outcome: string; runs: number }[];
  tools: AgentToolStats[];
  trend: AgentDayStats[];
};

export type AgentAnalyticsView = {
  agentId: string;
  days: number;
  from: string;
  to: string;
  total: AgentVersionStats;
  versions: AgentVersionStats[];
  detail: AgentAnalyticsDetail | null;
};

/** An agent action waiting for its approver (spec 027 US4); the API shows it only to someone who holds its task. */
export type AgentApprovalView = {
  id: string;
  runId: string;
  agentId: string;
  agentVersion: number;
  agentNames: Localized;
  requestedBy: string;
  requesterName: string | null;
  tool: string;
  parameters: Record<string, unknown>;
  hash: string;
  summary: string | null;
  status: "pending" | "approved" | "rejected" | "expired" | "cancelled";
  dueAt: string;
  createdAt: string;
  taskId: string | null;
};

/** An org unit's own agent rules (spec 027 US8 scenario 3); a null value inherits from the policy above. */
export type UnitAgentPolicy = {
  maxAutonomy: string | null;
  maxClassification: string | null;
  allowedTools: string[] | null;
  toolRules: Record<string, string> | null;
  scheduledAgents: boolean | null;
  persistentMemory: boolean | null;
  maxToolCalls: number | null;
  maxSteps: number | null;
  maxRunSeconds: number | null;
  maxDailyRunsPerAgent: number | null;
};

export type UnitAgentPolicyView = {
  unitId: string;
  version: number;
  policy: UnitAgentPolicy;
  inherited: AgentPolicy;
  effective: AgentPolicy;
  updatedAt: string | null;
};

/** Agent Security Center records (spec 027 FR-023). */
export const ALERT_RULES = ["denied_burst", "injection", "cost_spike"] as const;
export const SEVERITIES = ["low", "medium", "high", "critical"] as const;

export type AgentAlert = {
  id: string;
  agentId: string;
  agentNames: Localized;
  orgUnitId: string;
  rule: string;
  severity: string;
  status: "open" | "dismissed" | "incident" | string;
  matches: number;
  runIds: string[];
  details: Record<string, unknown>;
  incidentId: string | null;
  createdAt: string;
  updatedAt: string;
};

export type AgentIncident = {
  id: string;
  title: string;
  severity: string;
  status: "open" | "closed" | string;
  agentId: string | null;
  orgUnitId: string;
  runIds: string[];
  actions: { at: string; by: string; text: string }[];
  closureNote: string | null;
  /** Null when the platform opened it by itself, such as a limit breach of an L4 agent task (spec 027 R3). */
  openedBy: string | null;
  openedAt: string;
  closedBy: string | null;
  closedAt: string | null;
  version: number;
  alertIds: string[];
  kind: string | null;
};

export const INCIDENT_KINDS = ["l4_limit"] as const;
export type IncidentKind = (typeof INCIDENT_KINDS)[number];
export const incidentKind = (kind: string | null): IncidentKind | null => (kind !== null && (INCIDENT_KINDS as readonly string[]).includes(kind) ? (kind as IncidentKind) : null);

export type AgentWatch = { agentId: string; agentNames: Localized; orgUnitId: string; version: number; reason: string; since: string; until: string };
