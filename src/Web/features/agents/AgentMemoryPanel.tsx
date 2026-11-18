"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import type { AgentMemoryView, MemoryItem } from "@/lib/agents/types";

/**
 * What an agent remembers for the signed-in person (spec 020 US9): the offer to keep notes, which they accept per agent,
 * their notes with edit and delete, and withdrawing, which deletes them all. The API decides whether memory is offered
 * and owns every note; this panel only shows and sends.
 */
export function AgentMemoryPanel({ view }: { view: AgentMemoryView }) {
  const t = useTranslations("agents.memory");
  const router = useRouter();
  const ids = useId();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function send(path: string, method: string, body?: unknown) {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/agents${path}`, {
        method,
        headers: body === undefined ? undefined : { "content-type": "application/json" },
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

  return (
    <section className="agents-section" aria-labelledby={`${ids}-title`} data-testid="agent-memory">
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      {!view.accepted ? (
        view.offered ? (
          <>
            <p>{t("offer", { days: view.retentionDays })}</p>
            <div>
              <Button disabled={busy} onClick={() => send(`/${view.agentId}/memory/consent`, "PUT")}>
                {t("accept")}
              </Button>
            </div>
          </>
        ) : (
          <p>{t("notOffered")}</p>
        )
      ) : (
        <>
          <p>{t("accepted", { days: view.retentionDays })}</p>
          {view.items.length === 0 ? <StateView kind="empty" title={t("none")} message={t("noneHint")} /> : null}
        </>
      )}
      {view.items.length > 0 ? (
        <ul className="agents-access-list" data-testid="agent-memory-items">
          {view.items.map((item) => (
            <MemoryRow key={item.id} item={item} busy={busy} onSave={(text) => send(`/memory/${item.id}`, "PUT", { text })} onDelete={() => send(`/memory/${item.id}`, "DELETE")} />
          ))}
        </ul>
      ) : null}
      {view.accepted ? (
        <div>
          <ConfirmAction
            label={t("withdraw")}
            question={t("withdrawQuestion")}
            confirmLabel={t("withdrawConfirm")}
            cancelLabel={t("cancel")}
            onConfirm={() => send(`/${view.agentId}/memory/consent`, "DELETE")}
          />
        </div>
      ) : null}
      {messages.map((m) => (
        <p key={m} role="alert" className="field-error">
          {m}
        </p>
      ))}
    </section>
  );
}

function MemoryRow({ item, busy, onSave, onDelete }: { item: MemoryItem; busy: boolean; onSave: (text: string) => void; onDelete: () => void }) {
  const t = useTranslations("agents.memory");
  const format = useFormatter();
  const ids = useId();
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(item.text);
  return (
    <li>
      {editing ? (
        <div className="field">
          <label htmlFor={`${ids}-text`}>{t("note")}</label>
          <textarea id={`${ids}-text`} rows={2} maxLength={500} value={text} onChange={(e) => setText(e.target.value)} />
        </div>
      ) : (
        <span>
          <bdi>{item.text}</bdi>
          <span className="field-hint">
            {" · "}
            {t("lastUsed", { date: format.dateTime(new Date(item.lastUsedAt), { dateStyle: "medium" }) })}
          </span>
        </span>
      )}
      <span className="agents-market-actions">
        {editing ? (
          <>
            <Button size="sm" disabled={busy || text.trim().length === 0} onClick={() => onSave(text)}>
              {t("save")}
            </Button>
            <Button size="sm" variant="secondary" onClick={() => {
                setEditing(false);
                setText(item.text);
              }}>
              {t("cancel")}
            </Button>
          </>
        ) : (
          <>
            <Button size="sm" variant="secondary" disabled={busy} onClick={() => setEditing(true)}>
              {t("edit")}
            </Button>
            <Button size="sm" variant="secondary" disabled={busy} onClick={onDelete}>
              {t("delete")}
            </Button>
          </>
        )}
      </span>
    </li>
  );
}
