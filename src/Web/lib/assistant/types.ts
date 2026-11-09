/** Assistant API shapes (spec 014, /api/v1/assistant). */
export const assistantOutcomes = ["answered", "no_evidence", "policy_blocked", "unavailable"] as const;
export type AssistantOutcome = (typeof assistantOutcomes)[number];

export const feedbackReasons = ["incorrect", "not_relevant", "incomplete", "unclear", "other"] as const;
export type FeedbackReason = (typeof feedbackReasons)[number];

/** Reasons a turn was unavailable that have their own message; any other reason gets the general one. */
export const unavailableReasons = ["quota_exhausted", "budget_exhausted", "not_in_package", "provider_unavailable", "context_too_large", "refused"] as const;

export type Citation = {
  n: number;
  source: string;
  itemId: string;
  kind: string;
  title: string;
  anchor: string;
  locale: string;
  version: number | null;
  updatedAt: string | null;
};

export type Turn = {
  id: string;
  conversationId: string;
  question: string;
  answer: string | null;
  outcome: AssistantOutcome;
  reason: string | null;
  language: "ar" | "en";
  agent: string | null;
  citations: Citation[];
  withheld: boolean;
  retrievalDegraded: boolean;
  generated: boolean;
  createdAt: string;
  helpful: boolean | null;
};

export type ConversationSummary = { id: string; title: string; turns: number; createdAt: string; lastTurnAt: string };

export type Conversation = { id: string; title: string; createdAt: string; lastTurnAt: string; turns: Turn[] };

export type AssistantSettings = { retentionDays: number; updatedAt: string | null };

/** Splits an answer into text and citation markers, keeping only markers that have a citation. */
export function answerParts(answer: string, citations: Citation[]): ({ text: string } | { citation: Citation })[] {
  const byN = new Map(citations.map((c) => [c.n, c]));
  const parts: ({ text: string } | { citation: Citation })[] = [];
  let last = 0;
  for (const m of answer.matchAll(/\[(\d{1,3})\]/g)) {
    const citation = byN.get(Number(m[1]));
    if (!citation) continue;
    if (m.index > last) parts.push({ text: answer.slice(last, m.index) });
    parts.push({ citation });
    last = m.index + m[0].length;
  }
  if (last < answer.length) parts.push({ text: answer.slice(last) });
  return parts;
}

/** Reads server-sent events from a streamed turn: text deltas, then the stored turn or a failure. */
export async function readTurnStream(
  response: Response,
  onDelta: (text: string) => void,
): Promise<Turn | null> {
  const reader = response.body?.getReader();
  if (!reader) return null;
  const decoder = new TextDecoder();
  let buffer = "";
  let done: Turn | null = null;
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
        const payload = JSON.parse(data) as { text?: string } & Turn;
        if (event === "delta" && payload.text) onDelta(payload.text);
        if (event === "done") done = payload;
      }
      cut = buffer.indexOf("\n\n");
    }
    if (end) break;
  }
  return done;
}

export type TextAction = "summarize" | "explain" | "translate" | "rewrite" | "draft";

export type TextActionResult = {
  action: TextAction;
  outcome: AssistantOutcome;
  reason: string | null;
  text: string | null;
  language: "ar" | "en";
  source: Citation | null;
  withheld: boolean;
  generated: boolean;
};
