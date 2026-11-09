"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { ConfirmationCard, RunView } from "@/lib/agents/types";

type Turn = { message: string; run: RunView };

/** Shows a parameter value as plain text: text as it is, anything else as compact JSON. */
function show(value: unknown): string {
  return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * The confirmation card (spec 027 FR-010, US4): the exact action and parameters the agent asks to run, with values
 * that also appeared in content the agent read marked for a second look. Nothing runs until the person confirms, and
 * the API runs exactly what the card shows, bound by its hash.
 */
function Card({ card, busy, onDecide }: { card: ConfirmationCard; busy: boolean; onDecide: (confirm: boolean) => void }) {
  const ts = useTranslations("agents");
  const id = useId();
  const tool = ts.has(`toolNames.${card.tool}`) ? ts(`toolNames.${card.tool}`) : card.tool;
  return (
    <section className="agents-card" aria-labelledby={`${id}-title`} data-testid="agent-confirmation">
      <h3 id={`${id}-title`}>{ts("card.title")}</h3>
      <p>{ts("card.intro", { tool })}</p>
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
      <div className="agents-row">
        <Button variant="primary" busy={busy} busyLabel={ts("card.working")} onClick={() => onDecide(true)}>
          {ts("card.confirm")}
        </Button>
        <Button busy={busy} busyLabel={ts("card.working")} onClick={() => onDecide(false)}>
          {ts("card.cancel")}
        </Button>
      </div>
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

  function replace(run: RunView) {
    setTurns((all) => all.map((turn) => (turn.run.id === run.id ? { ...turn, run } : turn)));
  }

  async function ask() {
    const text = message.trim();
    if (text === "") return;
    setBusy(true);
    setError([]);
    try {
      const response = await fetch(`/api/v1/agents/${agentId}/runs`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ message: text, itemSource: turns.length === 0 ? item?.source : undefined, itemId: turns.length === 0 ? item?.id : undefined }),
      });
      if (response.ok) {
        const run = (await response.json()) as RunView;
        setTurns((all) => [...all, { message: text, run }]);
        setMessage("");
      } else {
        setError(await problemMessages(response));
      }
    } catch {
      setError([ts("use.failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function decide(run: RunView, card: ConfirmationCard, confirm: boolean) {
    setBusy(true);
    setError([]);
    try {
      const response = await fetch(`/api/v1/agents/runs/${run.id}/confirmations/${card.id}`, {
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
        {turns.map((turn) => (
          <li key={turn.run.id} className="agents-section">
            <p>
              <strong>{ts("use.you")}</strong> <bdi>{turn.message}</bdi>
            </p>
            {turn.run.answer ? (
              <div>
                <p className="agents-answer" data-testid="agent-answer">
                  {turn.run.answer}
                </p>
                {turn.run.generated ? <p className="field-hint">{ts("generated")}</p> : null}
              </div>
            ) : null}
            {turn.run.status === "waiting" && turn.run.confirmation ? (
              <Card card={turn.run.confirmation} busy={busy} onDecide={(confirm) => void decide(turn.run, turn.run.confirmation!, confirm)} />
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
        ))}
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
