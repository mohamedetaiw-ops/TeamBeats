"use client";

import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Drawer } from "@/components/ui/Dialog";
import { Icon } from "@/components/ui/Icon";
import { Kbd } from "@/components/ui/Status";
import { problemMessages } from "@/lib/notifications/types";
import { readTurnStream, type AssistantScope, type AssistantSuggestions, type Turn } from "@/lib/assistant/types";
import { TurnItem } from "./Assistant";
import "./assistant.css";

const MAX_QUESTION = 2000;

/** Ctrl+/ or Command+/ by the physical key, so the shortcut is the same on an Arabic keyboard. */
function isShortcut(event: KeyboardEvent) {
  return (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && (event.code === "Slash" || event.key === "/");
}

/**
 * The assistant panel in the shell (spec 014 FR-001, AST-001, T007; spec 008 FR-056 and FR-066): Ask TeamBeats in the
 * shell bar opens the assistant beside the page, on every page, and Ctrl+/ (Command+/ on a Mac) opens it from the
 * keyboard. The panel is a drawer from the end edge, so it mirrors in right to left, keeps focus inside and gives focus
 * back on close. Answers stream and cite their sources as text links, the same as the full page; the text tools,
 * files, voice and history stay on the full page, which the panel links to with the open conversation. On the
 * assistant page itself the button stays a link. Every rule (what is retrieved, cited or routed to an agent) is the API's.
 * On a page that shows one item the person may read, the panel offers the API's suggested questions about it before the
 * first turn (spec 014 R3, US9); picking one asks it kept to that item, and the person can turn suggestions off here.
 */
export function AssistantPanel() {
  const t = useTranslations("assistant");
  const ts = useTranslations("shell");
  const path = usePathname();
  const onPage = path === "/assistant";
  const [open, setOpen] = useState(false);
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [question, setQuestion] = useState("");
  const [pending, setPending] = useState<{ question: string; text: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState("");
  // Kept with the page they were asked for, so another page never shows them.
  const [suggested, setSuggested] = useState<{ path: string; view: AssistantSuggestions } | null>(null);
  const suggestions = suggested?.path === path ? suggested.view : null;
  const input = useRef<HTMLTextAreaElement>(null);
  const end = useRef<HTMLDivElement>(null);
  const inputId = useId();
  const hintId = useId();
  const suggestId = useId();

  useEffect(() => {
    if (onPage) return;
    const onKey = (event: KeyboardEvent) => {
      if (isShortcut(event)) {
        event.preventDefault();
        setOpen(true);
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onPage]);

  useEffect(() => {
    if (open) input.current?.focus();
  }, [open]);

  // Suggestions for this page, asked for when the panel opens on it; a failure just shows none.
  useEffect(() => {
    if (!open || onPage) return;
    let live = true;
    fetch(`/api/v1/assistant/suggestions?${new URLSearchParams({ path }).toString()}`)
      .then((r) => (r.ok ? (r.json() as Promise<AssistantSuggestions>) : null))
      .then((view) => {
        if (live && view) setSuggested({ path, view });
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [open, onPage, path]);

  async function turnOff() {
    const response = await fetch("/api/v1/assistant/suggestions/preference", {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ on: false }),
    }).catch(() => null);
    if (response?.ok) {
      setSuggested({ path, view: (await response.json()) as AssistantSuggestions });
      setStatus(t("suggestions.turnedOff"));
      input.current?.focus();
    }
  }

  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" });
  }, [turns.length, pending?.text]);

  function ask(e: FormEvent) {
    e.preventDefault();
    void send(question.trim(), null);
  }

  async function send(q: string, scope: AssistantScope | null) {
    if (!q || pending) return;
    setError(null);
    setStatus(t("thinking"));
    setPending({ question: q, text: "" });
    try {
      const response = await fetch("/api/v1/assistant/turns", {
        method: "POST",
        headers: { "content-type": "application/json", accept: "text/event-stream" },
        body: JSON.stringify({ question: q, conversationId, scope: conversationId === null ? scope : null, fileIds: [] }),
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
      if (scope === null) setQuestion("");
      setConversationId(done.conversationId);
      setTurns((list) => [...list, done]);
      setStatus(t("answerReady"));
    } catch {
      setError(t("failed"));
      setStatus("");
    } finally {
      setPending(null);
      input.current?.focus();
    }
  }

  function startOver() {
    setConversationId(null);
    setTurns([]);
    setError(null);
    setStatus("");
    input.current?.focus();
  }

  const label = (
    <>
      <Icon name="spark" size={18} />
      <span>{ts("ask")}</span>
    </>
  );

  if (onPage) {
    return (
      <Link className="shell-ask" href="/assistant" aria-current="page">
        {label}
      </Link>
    );
  }

  const fullPage = conversationId ? `/assistant?c=${encodeURIComponent(conversationId)}` : "/assistant";

  return (
    <>
      <button type="button" className="shell-ask" aria-haspopup="dialog" aria-expanded={open} aria-keyshortcuts="Control+Slash Meta+Slash" onClick={() => setOpen(true)}>
        {label}
      </button>
      <Drawer
        open={open}
        onClose={() => setOpen(false)}
        side="end"
        title={t("title")}
        closeLabel={t("panel.close")}
        footer={
          open ? (
            <>
              {conversationId ? (
                <Button variant="quiet" icon="plus" onClick={startOver}>
                  {t("history.new")}
                </Button>
              ) : null}
              <ButtonLink variant="secondary" icon="arrow-forward" href={fullPage}>
                {t("panel.fullPage")}
              </ButtonLink>
            </>
          ) : null
        }
      >
        {/* The dialog stays in the page while closed, so its content is drawn only while it is open. */}
        {open ? (
          <div className="assistant-panel" data-testid="assistant-panel">
            {turns.length === 0 && !pending ? (
              <p className="assistant-intro">
                {t.rich("panel.intro", { key: (chunks) => <Kbd>{chunks}</Kbd> })}
              </p>
            ) : null}
            {turns.length === 0 && !pending && suggestions && suggestions.suggestions.length > 0 ? (
              <section className="assistant-suggestions" aria-labelledby={suggestId} data-testid="assistant-suggestions">
                <h3 id={suggestId}>{t("suggestions.title")}</h3>
                <ul>
                  {suggestions.suggestions.map((s) => (
                    <li key={s.key}>
                      <Button variant="secondary" icon="spark" onClick={() => void send(t(`suggestions.question.${s.key}`, { title: s.title }), s.scope)}>
                        {t(`suggestions.label.${s.key}`)}
                      </Button>
                    </li>
                  ))}
                </ul>
                <Button variant="quiet" onClick={() => void turnOff()}>
                  {t("suggestions.turnOff")}
                </Button>
              </section>
            ) : null}
            <ol className="assistant-turns" aria-busy={pending ? true : undefined}>
              {turns.map((turn) => (
                <TurnItem key={turn.id} turn={turn} voice={false} />
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
            <form className="assistant-composer" onSubmit={ask}>
              <label htmlFor={inputId}>{t("label")}</label>
              <textarea
                id={inputId}
                ref={input}
                value={question}
                rows={2}
                maxLength={MAX_QUESTION}
                required
                dir="auto"
                aria-describedby={hintId}
                onChange={(e) => setQuestion(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
                    e.preventDefault();
                    e.currentTarget.form?.requestSubmit();
                  }
                }}
              />
              <div className="assistant-composer-bar">
                <p id={hintId} className="field-hint">
                  {t("hint")}
                </p>
                <Button type="submit" variant="primary" busy={pending !== null} busyLabel={t("thinking")}>
                  {t("ask")}
                </Button>
              </div>
            </form>
          </div>
        ) : null}
      </Drawer>
    </>
  );
}
