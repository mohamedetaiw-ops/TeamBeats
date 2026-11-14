"use client";

import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { Menu } from "@/components/ui/Menu";
import { problemMessages } from "@/lib/notifications/types";
import {
  answerParts,
  feedbackReasons,
  readTurnStream,
  unavailableReasons,
  type Conversation,
  type ConversationSummary,
  type FeedbackReason,
  type AssistantScope,
  type TextAction,
  type TextActionResult,
  type Turn,
} from "@/lib/assistant/types";
import { Result, runAction } from "./TextActions";

const textActions = ["rewrite", "draft", "summarize", "explain", "translate"] as const;
const starters = ["leave", "policy", "service", "people"] as const;
type Mode = "ask" | TextAction;
/** A text tool's run: it lives in the transcript of this visit only, since nothing about it is stored (FR-007). */
type ToolRun = { id: number; action: TextAction; text: string; result: TextActionResult | null; error: string | null };

const MAX_QUESTION = 2000;

function isKnown<T extends string>(list: readonly T[], value: string | null): value is T {
  return value !== null && (list as readonly string[]).includes(value);
}

/** Feedback on one answer (US4 scenario 2): helpful or not, and for "not helpful" an optional reason. */
function Feedback({ turn }: { turn: Turn }) {
  const t = useTranslations("assistant");
  const [helpful, setHelpful] = useState<boolean | null>(turn.helpful);
  const [asking, setAsking] = useState(false);
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);
  const reasonId = useId();

  async function send(value: boolean, reason?: FeedbackReason) {
    setMessage(null);
    try {
      const response = await fetch(`/api/v1/assistant/turns/${encodeURIComponent(turn.id)}/feedback`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ helpful: value, reason: reason ?? null }),
      });
      if (!response.ok) {
        setMessage({ error: true, text: (await problemMessages(response)).join(" ") || t("failed") });
        return;
      }
      setHelpful(value);
      setAsking(!value && !reason);
      setMessage({ error: false, text: t("feedback.thanks") });
    } catch {
      setMessage({ error: true, text: t("failed") });
    }
  }

  return (
    <div className="assistant-feedback">
      <span>{t("feedback.question")}</span>
      <Button variant="quiet" aria-pressed={helpful === true} onClick={() => void send(true)}>
        {t("feedback.helpful")}
      </Button>
      <Button variant="quiet" aria-pressed={helpful === false} onClick={() => void send(false)}>
        {t("feedback.notHelpful")}
      </Button>
      {asking ? (
        <div className="assistant-feedback-reason">
          <label htmlFor={reasonId}>{t("feedback.reason")}</label>
          <select id={reasonId} defaultValue="" onChange={(e) => isKnown(feedbackReasons, e.target.value) && void send(false, e.target.value)}>
            <option value="" disabled>
              {t("feedback.choose")}
            </option>
            {feedbackReasons.map((r) => (
              <option key={r} value={r}>
                {t(`feedback.reasons.${r}`)}
              </option>
            ))}
          </select>
        </div>
      ) : null}
      <span aria-live="polite">
        {message ? (
          <span className={message.error ? "field-error" : "assistant-thanks"} role={message.error ? "alert" : undefined}>
            {message.text}
          </span>
        ) : null}
      </span>
    </div>
  );
}

/** One turn: the question, then the answer with its citations, or the message for why there is none (FR-011). */
function TurnItem({ turn }: { turn: Turn }) {
  const t = useTranslations("assistant");
  const tk = useTranslations("search.kinds");
  const format = useFormatter();
  const kind = (k: string) => (isKnown(["person", "content", "document", "knowledge", "policy", "service"] as const, k) ? tk(k) : k);
  const reason = isKnown(unavailableReasons, turn.reason) ? turn.reason : "other";

  return (
    <li className="assistant-turn">
      <div className="assistant-question" dir="auto">
        <span className="visually-hidden">{t("you")}</span>
        <p>{turn.question}</p>
      </div>
      <div className="assistant-answer" lang={turn.language} dir={turn.language === "ar" ? "rtl" : "ltr"}>
        {turn.outcome === "answered" && turn.answer ? (
          <>
            <p className="assistant-label">{t("generated")}</p>
            <p className="assistant-text">
              {answerParts(turn.answer, turn.citations).map((part, i) =>
                "text" in part ? (
                  <span key={i}>{part.text}</span>
                ) : (
                  <a key={i} className="assistant-marker" href={part.citation.anchor} aria-label={t("citation", { n: part.citation.n, title: part.citation.title })}>
                    {t("marker", { n: part.citation.n })}
                  </a>
                ),
              )}
            </p>
            {turn.citations.length > 0 ? (
              <div className="assistant-sources">
                <h3>{t("sources")}</h3>
                <ol>
                  {turn.citations.map((c) => (
                    <li key={c.n} value={c.n}>
                      <a href={c.anchor}>
                        <bdi>{c.title}</bdi>
                      </a>{" "}
                      <span className="assistant-source-meta">
                        {kind(c.kind)}
                        {c.version !== null ? ` · ${t("version", { version: c.version })}` : ""}
                        {c.updatedAt ? ` · ${format.dateTime(new Date(c.updatedAt), { dateStyle: "medium" })}` : ""}
                      </span>
                    </li>
                  ))}
                </ol>
              </div>
            ) : (
              <p className="notice">{t("uncited")}</p>
            )}
          </>
        ) : turn.outcome === "no_evidence" ? (
          // Nothing the person can open supports an answer: said plainly, with its own look and a next step (FR-026).
          <Alert
            tone="warning"
            title={t("cannotVerify.title")}
            action={
              <ButtonLink size="sm" variant="secondary" icon="search" href={`/search?q=${encodeURIComponent(turn.question)}`}>
                {t("cannotVerify.search")}
              </ButtonLink>
            }
          >
            <p>{t("outcomes.no_evidence")}</p>
          </Alert>
        ) : turn.outcome === "policy_blocked" ? (
          <p className="notice">{t("outcomes.policy_blocked")}</p>
        ) : (
          <p className="notice" role="alert">
            {t(`unavailable.${reason}`)}
          </p>
        )}
        {turn.outcome === "answered" && turn.withheld ? <p className="assistant-note">{t("withheld")}</p> : null}
        {turn.retrievalDegraded ? <p className="assistant-note">{t("degraded")}</p> : null}
      </div>
      {turn.outcome === "answered" ? <Feedback turn={turn} /> : null}
    </li>
  );
}

/**
 * The assistant (spec 014 US1, US3 and US4, FR-001, FR-003, FR-011, FR-014, FR-024, FR-025): one conversation
 * workspace. The question streams its answer; citations are text links into the product, and answers are plain text,
 * so no image or outside link is ever loaded. The text tools (rewrite, draft, summarize, explain, translate) are modes
 * of the same composer and their results join the transcript; there is no second form. Every rule (what may be
 * retrieved, cited or sent to a model) is the API's.
 */
export function AssistantChat({
  initial,
  conversations,
  scope = null,
  uiLocale,
}: {
  initial: Conversation | null;
  conversations: ConversationSummary[];
  uiLocale: string;
  /** Keeps a new conversation to one item; the API keeps it for every later turn. */
  scope?: AssistantScope | null;
}) {
  const t = useTranslations("assistant");
  const router = useRouter();
  const format = useFormatter();
  const [turns, setTurns] = useState<Turn[]>(initial?.turns ?? []);
  const [question, setQuestion] = useState("");
  const [pending, setPending] = useState<{ question: string; text: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState("");
  const [mode, setMode] = useState<Mode>("ask");
  const [instructions, setInstructions] = useState("");
  const [language, setLanguage] = useState(uiLocale === "ar" ? "en" : "ar");
  const [runs, setRuns] = useState<ToolRun[]>([]);
  const [working, setWorking] = useState(false);
  const inputId = useId();
  const hintId = useId();
  const modeId = useId();
  const instructionsId = useId();
  const languageId = useId();
  const input = useRef<HTMLTextAreaElement>(null);
  const end = useRef<HTMLDivElement>(null);
  const conversationId = initial?.id ?? null;

  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" });
  }, [turns.length, pending?.text, runs.length]);

  // A text tool works on the text in the composer; its result joins the transcript, labelled as generated.
  async function runTool(action: TextAction) {
    const text = question.trim();
    if (working || (!text && !(action === "draft" && instructions.trim()))) return;
    setError(null);
    setWorking(true);
    setStatus(t("thinking"));
    const run: ToolRun = { id: Date.now(), action, text: text || instructions.trim(), result: null, error: null };
    try {
      const { result, error: problem, status: code } = await runAction({
        action,
        text: text || null,
        instructions: instructions.trim() || null,
        language: action === "translate" ? language : null,
      });
      setRuns((list) => [...list, { ...run, result, error: result ? null : code === 403 ? t("notAvailable") : (problem ?? t("failed")) }]);
      if (result) {
        setQuestion("");
        setInstructions("");
      }
      setStatus(result ? t("answerReady") : "");
    } catch {
      setRuns((list) => [...list, { ...run, error: t("failed") }]);
      setStatus("");
    } finally {
      setWorking(false);
      input.current?.focus();
    }
  }

  async function ask(e: FormEvent) {
    e.preventDefault();
    if (mode !== "ask") {
      await runTool(mode);
      return;
    }
    const q = question.trim();
    if (!q || pending) return;
    setError(null);
    setStatus(t("thinking"));
    setPending({ question: q, text: "" });
    try {
      const response = await fetch("/api/v1/assistant/turns", {
        method: "POST",
        headers: { "content-type": "application/json", accept: "text/event-stream" },
        body: JSON.stringify({ question: q, conversationId, scope: conversationId === null ? scope : null }),
      });
      if (!response.ok) {
        setError(response.status === 403 ? t("notAvailable") : (await problemMessages(response)).join(" ") || t("failed"));
        setStatus("");
        return;
      }
      const done = await readTurnStream(response, (delta) => setPending((p) => (p ? { ...p, text: p.text + delta } : p)));
      if (!done) {
        setError(t("failed"));
        setStatus("");
        return;
      }
      setQuestion("");
      setStatus(t("answerReady"));
      if (conversationId === null) {
        router.replace(`/assistant?c=${encodeURIComponent(done.conversationId)}`);
      } else {
        setTurns((list) => [...list, done]);
        router.refresh();
      }
    } catch {
      setError(t("failed"));
      setStatus("");
    } finally {
      setPending(null);
      input.current?.focus();
    }
  }

  // Deleting asks first in a dialog that says how much goes (spec 014 FR-023, DLS-084).
  const [confirming, setConfirming] = useState<"one" | "all" | null>(null);

  async function remove(all: boolean) {
    const url = all ? "/api/v1/assistant/conversations" : `/api/v1/assistant/conversations/${encodeURIComponent(conversationId ?? "")}`;
    const response = await fetch(url, { method: "DELETE" }).catch(() => null);
    if (!response?.ok) {
      setError(t("failed"));
      return;
    }
    router.replace("/assistant");
    router.refresh();
  }

  return (
    <div className="assistant">
      <aside className="assistant-history" aria-labelledby="assistant-history-title">
        <h2 id="assistant-history-title">{t("history.title")}</h2>
        <Link className="button button-secondary" href="/assistant">
          {t("history.new")}
        </Link>
        {conversations.length === 0 ? (
          <p>{t("history.empty")}</p>
        ) : (
          <ul>
            {conversations.map((c) => (
              <li key={c.id}>
                <Link href={`/assistant?c=${encodeURIComponent(c.id)}`} aria-current={c.id === conversationId ? "page" : undefined}>
                  <bdi>{c.title}</bdi>
                </Link>
                <time dateTime={c.lastTurnAt}>{format.dateTime(new Date(c.lastTurnAt), { dateStyle: "medium" })}</time>
              </li>
            ))}
          </ul>
        )}
        {conversations.length > 0 ? (
          <Menu
            label={t("history.more")}
            icon="more"
            variant="quiet"
            size="sm"
            items={[{ key: "delete-all", label: t("history.deleteAll"), icon: "trash", danger: true, onSelect: () => setConfirming("all") }]}
          />
        ) : null}
        <p className="assistant-retention">{t("history.retention")}</p>
      </aside>

      <Dialog
        open={confirming !== null}
        onClose={() => setConfirming(null)}
        size="sm"
        title={confirming === "all" ? t("history.confirmAllTitle", { count: conversations.length }) : t("history.confirmOneTitle")}
        description={t("history.cannotUndo")}
        closeLabel={t("history.cancel")}
        footer={
          <>
            <Button variant="secondary" onClick={() => setConfirming(null)}>
              {t("history.cancel")}
            </Button>
            <Button
              variant="primary"
              className="button-danger"
              onClick={() => {
                const all = confirming === "all";
                setConfirming(null);
                void remove(all);
              }}
            >
              {t("history.confirmDelete")}
            </Button>
          </>
        }
      />

      <section className="assistant-main" aria-labelledby="assistant-conversation-title">
        <div className="assistant-main-head">
          <h2 id="assistant-conversation-title">{initial ? <bdi>{initial.title}</bdi> : t("newConversation")}</h2>
          {initial ? (
            <Button variant="quiet" icon="trash" onClick={() => setConfirming("one")}>
              {t("history.delete")}
            </Button>
          ) : null}
        </div>

        {turns.length === 0 && runs.length === 0 && !pending && !scope ? (
          <div className="assistant-start">
            <p className="assistant-intro">{t("starters.title")}</p>
            <ul className="assistant-starters">
              {starters.map((key) => (
                <li key={key}>
                  <button
                    type="button"
                    className="ui-chip"
                    data-selectable
                    onClick={() => {
                      setMode("ask");
                      setQuestion(t(`starters.${key}`));
                      input.current?.focus();
                    }}
                  >
                    {t(`starters.${key}`)}
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
        <ol className="assistant-turns" aria-busy={pending ? true : undefined}>
          {turns.map((turn) => (
            <TurnItem key={turn.id} turn={turn} />
          ))}
          {runs.map((run) => (
            <li key={run.id} className="assistant-turn">
              <div className="assistant-question" dir="auto">
                <span className="assistant-label">{t(`tools.actions.${run.action}`)}</span>
                <p>{run.text}</p>
              </div>
              <div className="assistant-answer">
                {run.result ? <Result result={run.result} /> : null}
                {run.error ? (
                  <p className="field-error" role="alert">
                    {run.error}
                  </p>
                ) : null}
              </div>
            </li>
          ))}
          {pending ? (
            <li className="assistant-turn">
              <div className="assistant-question" dir="auto">
                <span className="visually-hidden">{t("you")}</span>
                <p>{pending.question}</p>
              </div>
              <div className="assistant-answer" dir="auto">
                <p className="assistant-label">{t("thinking")}</p>
                {pending.text ? <p className="assistant-text">{pending.text}</p> : null}
              </div>
            </li>
          ) : null}
        </ol>
        <div ref={end} />

        <p className="visually-hidden" role="status">
          {status}
        </p>
        {error ? (
          <p className="field-error" role="alert">
            {error}
          </p>
        ) : null}

        <form className="assistant-composer" onSubmit={(e) => void ask(e)}>
          <label htmlFor={inputId}>{mode === "ask" ? t("label") : mode === "draft" ? t("tools.draftText") : t("tools.text")}</label>
          <textarea
            id={inputId}
            ref={input}
            value={question}
            rows={mode === "ask" ? 2 : 5}
            maxLength={mode === "ask" ? MAX_QUESTION : 20000}
            required={mode !== "draft"}
            dir="auto"
            aria-describedby={hintId}
            onChange={(e) => setQuestion(e.target.value)}
            onKeyDown={(e) => {
              // In a text tool Enter starts a new line, since pasted text is edited there.
              if (mode === "ask" && e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
                e.preventDefault();
                e.currentTarget.form?.requestSubmit();
              }
            }}
          />
          {mode !== "ask" ? (
            <div className="field">
              <label htmlFor={instructionsId}>{mode === "draft" ? t("tools.brief") : t("tools.instructions")}</label>
              <input
                id={instructionsId}
                value={instructions}
                maxLength={500}
                dir="auto"
                required={mode === "draft" && question.trim().length === 0}
                onChange={(e) => setInstructions(e.target.value)}
              />
            </div>
          ) : null}
          <div className="assistant-composer-bar">
            <label className="ui-toolbar-select" htmlFor={modeId}>
              <span>{t("mode.label")}</span>
              <select id={modeId} value={mode} onChange={(e) => setMode(e.target.value as Mode)}>
                <option value="ask">{t("mode.ask")}</option>
                {textActions.map((a) => (
                  <option key={a} value={a}>
                    {t(`tools.actions.${a}`)}
                  </option>
                ))}
              </select>
            </label>
            {mode === "translate" ? (
              <label className="ui-toolbar-select" htmlFor={languageId}>
                <span>{t("tools.language")}</span>
                <select id={languageId} value={language} onChange={(e) => setLanguage(e.target.value)}>
                  <option value="ar">{t("tools.languages.ar")}</option>
                  <option value="en">{t("tools.languages.en")}</option>
                </select>
              </label>
            ) : null}
            <p id={hintId} className="field-hint">
              {mode === "ask" ? t("hint") : t("tools.intro")}
            </p>
            <Button type="submit" variant="primary" busy={pending !== null || working} busyLabel={t("thinking")}>
              {mode === "ask" ? t("ask") : t("tools.run")}
            </Button>
          </div>
        </form>
      </section>
    </div>
  );
}
