"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { IconButton } from "@/components/ui/IconButton";
import { Badge } from "@/components/ui/Badge";
import { problemMessages } from "@/lib/notifications/types";
import type { MarketplaceAgent } from "@/lib/agents/types";

const SCORES = [1, 2, 3, 4, 5] as const;

/**
 * The actions on one marketplace agent (spec 020 US6): favourite and rate an agent the person may use, or ask its
 * owners for access. The API decides who may do each, and the page reloads its list from the API afterwards.
 */
export function MarketplaceActions({
  agent,
  name,
}: {
  agent: MarketplaceAgent;
  name: string;
}) {
  const t = useTranslations("agents.marketplace");
  const router = useRouter();
  const ids = useId();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [score, setScore] = useState(
    agent.myScore ? String(agent.myScore) : "",
  );
  const [comment, setComment] = useState("");

  async function send(path: string, method: string, body?: unknown) {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/agents/${agent.id}${path}`, {
        method,
        headers:
          body === undefined
            ? undefined
            : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        router.refresh();
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    }
    setBusy(false);
  }

  if (!agent.canUse) {
    return (
      <div className="agents-market-actions">
        {agent.request === "pending" ? (
          <Badge tone="info" icon="clock">
            {t("requested")}
          </Badge>
        ) : (
          <>
            {agent.request === "denied" ? (
              <Badge tone="warning">{t("denied")}</Badge>
            ) : null}
            <Button
              variant="secondary"
              size="sm"
              disabled={busy}
              onClick={() => send("/access-requests", "POST")}
            >
              {t("requestAccess")}
            </Button>
          </>
        )}
        {messages.map((m) => (
          <p key={m} role="alert" className="field-error">
            {m}
          </p>
        ))}
      </div>
    );
  }

  return (
    <div className="agents-market-actions">
      <IconButton
        icon="star"
        size="sm"
        variant={agent.favourite ? "secondary" : "quiet"}
        label={
          agent.favourite
            ? t("unfavourite", { name })
            : t("favourite", { name })
        }
        aria-pressed={agent.favourite}
        disabled={busy}
        onClick={() => send("/favourite", agent.favourite ? "DELETE" : "PUT")}
      />
      <details className="agents-market-rate">
        <summary>
          {agent.myScore
            ? t("yourRating", { score: agent.myScore })
            : t("rate")}
        </summary>
        <div className="field">
          <label htmlFor={`${ids}-score`}>{t("score")}</label>
          <select
            id={`${ids}-score`}
            value={score}
            onChange={(e) => setScore(e.target.value)}
          >
            <option value="">{t("chooseScore")}</option>
            {SCORES.map((s) => (
              <option key={s} value={s}>
                {t("scoreValue", { score: s })}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor={`${ids}-comment`}>{t("comment")}</label>
          <textarea
            id={`${ids}-comment`}
            rows={2}
            maxLength={1000}
            value={comment}
            onChange={(e) => setComment(e.target.value)}
          />
          <p className="field-hint">{t("commentHint")}</p>
        </div>
        <Button
          size="sm"
          disabled={busy || !score}
          onClick={() =>
            send("/rating", "PUT", {
              score: Number(score),
              comment: comment.trim() || null,
            })
          }
        >
          {t("saveRating")}
        </Button>
      </details>
      {messages.map((m) => (
        <p key={m} role="alert" className="field-error">
          {m}
        </p>
      ))}
    </div>
  );
}
