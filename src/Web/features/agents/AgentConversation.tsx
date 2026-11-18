"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { readRunStream, type ConfirmationCard, type RunView } from "@/lib/agents/types";

/** A turn: the finished run, or only its ID when the connection dropped while the run went on (spec 020 T021). */
type Turn = { key: string; message: string; run: RunView | null; runId: string | null };

/** The turn being streamed: the answer text so far and the tool the agent is using. */
type Live = { message: string; text: string; tool: string | null };

/** Shows a parameter value as plain text: text as it is, anything else as compact JSON. */
function show(value: unknown): string {
  return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * The confirmation card (spec 027 FR-010, US4): the exact action and parameters the agent asks to run, with values
 * that also appeared in content the agent read marked for a second look. Nothing runs until the person confirms, and
 * the API runs exactly what the card shows, bound by its hash. An L3 action waits for an approver instead: the card
 * says so, and the person can stop the run or check for the answer once it was decided.
 */
function Card({ card, busy, onDecide, onCheck }: { card: ConfirmationCard; busy: boolean; onDecide: (confirm: boolean) => void; onCheck: () => void }) {
  const ts = useTranslations("agents");
  const id = useId();
  const tool = ts.has(`toolNames.${card.tool}`) ? ts(`toolNames.${card.tool}`) : card.tool;
  return (
    <section className="agents-card" aria-labelledby={`${id}-title`} data-testid="agent-confirmation">
      <h3 id={`${id}-title`}>{card.awaitingApproval ? ts("card.approvalTitle") : ts("card.title")}</h3>
      <p>{card.awaitingApproval ? ts("card.approvalIntro", { tool }) : ts("card.intro", { tool })}</p>
      <dl>
        {Object.entries(card.parameters).map(([key, value]) => (
          <div key={key} className="agents-card-row">
            <dt>
              <bdi>{key}</bdi>
            </dt>
            <dd>
              <bdi>{show(value)}</bdi>
              {card.fromContent.includes(key) ? <span className="agents-badge">{ts("card.fromContent")}</span> : null}
            </dd>
          </div>
        ))}
      </dl>
      {card.awaitingApproval ? (
        <div className="agents-row">
          <Button variant="primary" busy={busy} busyLabel={ts("card.working")} onClick={onCheck}>
            {ts("use.checkResult")}
          </Button>
          <Button busy={busy} busyLabel={ts("card.working")} onClick={() => onDecide(false)}>
            {ts("card.stop")}
          </Button>
        </div>
      ) : (
        <div className="agents-row">
          <Button variant="primary" busy={busy} busyLabel={ts("card.working")} onClick={() => onDecide(true)}>
            {ts("card.confirm")}
          </Button>
          <Button busy={busy} busyLabel={ts("card.working")} onClick={() => onDecide(false)}>
            {ts("card.cancel")}
          </Button>
        </div>
      )}
    </section>
  );
}

/**
 * A conversation with one agent (spec 020 US7, T014): the person's message, the agent's answer with its numbered
 * sources, and the confirmation card when the agent asks to act. The agent works with the person's permissions only;
 * the API checks every tool call.
 */
export function AgentConversation({ agentId, item }: { agentId: string; item: { source: string; id: string } | null }) {
  const ts = useTranslations("agents");
  const ids = useId();
  const [message, setMessage] = useState("");
  const [turns, setTurns] = useState<Turn[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string[]>([]);
  const [live, setLive] = useState<Live | null>(null);
  const [checking, setChecking] = useState<string | null>(null);

  function replace(run: RunView) {
    setTurns((all) => all.map((turn) => (turn.runId === run.id ? { ...turn, run } : turn)));
  }

  function toolName(tool: string | null) {
    if (!tool) return "";
    return ts.has(`toolNames.${tool}`) ? ts(`toolNames.${tool}`) : tool;
  }

  // The answer of a run whose stream broke: the run went on without the page, and keeps its result.
  async function check(runId: string) {
    setChecking(runId);
    setError([]);
    try {
      const response = await fetch(`/api/v1/agents/runs/${runId}/result`);
      if (!response.ok) {
        setError(await problemMessages(response));
        return;
      }
      const run = (await response.json()) as RunView;
      if (run.status === "running") setError([ts("use.stillWorking")]);
      else if (run.status === "waiting" && run.confirmation?.awaitingApproval) setError([ts("use.stillWaiting")]);
      else replace(run);
    } catch {
      setError([ts("use.failed")]);
    } finally {
      setChecking(null);
    }
  }

  async function ask() {
    const text = message.trim();
    if (text === "") return;
    setBusy(true);
    setError([]);
    let runId: string | null = null;
    try {
      const response = await fetch(`/api/v1/agents/${agentId}/runs`, {
        method: "POST",
        headers: { "content-type": "application/json", accept: "text/event-stream" },
        body: JSON.stringify({ message: text, itemSource: turns.length === 0 ? item?.source : undefined, itemId: turns.length === 0 ? item?.id : undefined }),
      });
      if (!response.ok) {
        setError(await problemMessages(response));
        return;
      }
      setLive({ message: text, text: "", tool: null });
      const run = await readRunStream(response, {
        onRun: (id) => {
          runId = id;
        },
        // A tool step ends the text of that model turn; the answer comes in a later one.
        onStep: (step) => setLive((l) => (l ? { ...l, text: "", tool: step.tool } : l)),
        onDelta: (delta) => setLive((l) => (l ? { ...l, text: l.text + delta } : l)),
      });
      setMessage("");
      setTurns((all) => [...all, { key: run?.id ?? runId ?? String(all.length), message: text, run, runId: run?.id ?? runId }]);
      if (!run && !runId) setError([ts("use.failed")]);
    } catch {
      if (runId) {
        const id: string = runId;
        setMessage("");
        setTurns((all) => [...all, { key: id, message: text, run: null, runId: id }]);
      } else {
        setError([ts("use.failed")]);
      }
    } finally {
      setLive(null);
      setBusy(false);
    }
  }

  async function decide(run: RunView, card: ConfirmationCard, confirm: boolean) {
    setBusy(true);
    setError([]);
    try {
      // An action waiting for an approver is not the person's to confirm; they can only stop the run.
      const response = card.awaitingApproval
        ? await fetch(`/api/v1/agents/runs/${run.id}/stop`, { method: "POST" })
        : await fetch(`/api/v1/agents/runs/${run.id}/confirmations/${card.id}`, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ confirm, hash: card.hash }),
          });
      if (response.ok) replace((await response.json()) as RunView);
      else setError(await problemMessages(response));
    } catch {
      setError([ts("use.failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="agents">
      {item ? <p className="notice">{ts("use.withItem")}</p> : null}
      <ol className="agents-timeline" aria-label={ts("use.conversation")} data-testid="agent-conversation">
        {turns.map((turn) =>
          turn.run === null ? (
            <li key={turn.key} className="agents-section">
              <p>
                <strong>{ts("use.you")}</strong> <bdi>{turn.message}</bdi>
              </p>
              <p role="status">{ts("use.interrupted")}</p>
              {turn.runId ? (
                <div className="agents-row">
                  <Button busy={checking === turn.runId} busyLabel={ts("use.working")} onClick={() => void check(turn.runId!)}>
                    {ts("use.checkResult")}
                  </Button>
                </div>
              ) : null}
            </li>
          ) : (
            <li key={turn.key} className="agents-section">
              <p>
                <strong>{ts("use.you")}</strong> <bdi>{turn.message}</bdi>
              </p>
              {turn.run.answer ? (
                <div>
                  <p className="agents-answer" data-testid="agent-answer">
                    {turn.run.answer}
                  </p>
                  {turn.run.generated ? <p className="field-hint">{ts("generated")}</p> : null}
                  {turn.run.linksAsText ? <p className="field-hint">{ts("use.linksAsText")}</p> : null}
                </div>
              ) : null}
              {turn.run.status === "waiting" && turn.run.confirmation ? (
                <Card
                  card={turn.run.confirmation}
                  busy={busy || checking === turn.run.id}
                  onDecide={(confirm) => void decide(turn.run!, turn.run!.confirmation!, confirm)}
                  onCheck={() => void check(turn.run!.id)}
                />
              ) : null}
              {!turn.run.answer && turn.run.status !== "waiting" ? <p role="status">{ts(`outcomes.${turn.run.outcome ?? "none"}`)}</p> : null}
              {turn.run.citations.length > 0 ? (
                <ol aria-label={ts("use.sources")}>
                  {turn.run.citations.map((c) => (
                    <li key={c.n}>
                      <Link href={c.link}>
                        <bdi>{c.title}</bdi>
                      </Link>
                    </li>
                  ))}
                </ol>
              ) : null}
            </li>
          ),
        )}
        {live ? (
          <li className="agents-section" data-testid="agent-live">
            <p>
              <strong>{ts("use.you")}</strong> <bdi>{live.message}</bdi>
            </p>
            {live.tool ? <p role="status">{ts("use.step", { tool: toolName(live.tool) })}</p> : null}
            {live.text ? <p className="agents-answer">{live.text}</p> : null}
          </li>
        ) : null}
      </ol>
      {error.length > 0 ? (
        <div role="alert" className="notice notice-error">
          {error.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <form
        className="agents-section"
        onSubmit={(e) => {
          e.preventDefault();
          void ask();
        }}
      >
        <div className="field">
          <label htmlFor={`${ids}-message`}>{ts("use.message")}</label>
          <textarea id={`${ids}-message`} rows={3} value={message} onChange={(e) => setMessage(e.target.value)} />
        </div>
        <div className="agents-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={ts("use.working")} disabled={message.trim() === ""}>
            {ts("use.send")}
          </Button>
        </div>
      </form>
    </div>
  );
}
