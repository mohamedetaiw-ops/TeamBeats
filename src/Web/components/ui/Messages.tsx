"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";

/**
 * Messages with priority and dismissal rules (FR-007). Critical messages persist until the user handles them and are
 * announced assertively. Normal and low messages never block the task: they sit outside the content, announce
 * politely and dismiss themselves (low sooner), or on demand. A message with an action, such as undo, stays long enough
 * to use it, and timers pause while the pointer or focus is on the message.
 */
export type MessagePriority = "critical" | "normal" | "low";

export type Message = {
  id: number;
  priority: MessagePriority;
  text: string;
  action?: { label: string; run: () => void };
};

type Show = (message: Omit<Message, "id">) => number;

const MessagesContext = createContext<{ show: Show; dismiss: (id: number) => void } | null>(null);

/** Milliseconds before a message dismisses itself; critical ones never do. */
export const lifetimes: Record<MessagePriority, number | null> = { critical: null, normal: 10000, low: 5000 };
const actionLifetime = 15000;

export function MessagesProvider({ children }: { children: ReactNode }) {
  const [messages, setMessages] = useState<Message[]>([]);
  const next = useRef(1);
  const dismiss = useCallback((id: number) => setMessages((all) => all.filter((m) => m.id !== id)), []);
  const show = useCallback<Show>((message) => {
    const id = next.current++;
    setMessages((all) => [...all, { ...message, id }]);
    return id;
  }, []);
  const value = useMemo(() => ({ show, dismiss }), [show, dismiss]);
  return (
    <MessagesContext.Provider value={value}>
      {children}
      <MessageList messages={messages} dismiss={dismiss} />
    </MessagesContext.Provider>
  );
}

export function useMessages() {
  const context = useContext(MessagesContext);
  if (!context) throw new Error("useMessages needs MessagesProvider");
  return context;
}

function MessageList({ messages, dismiss }: { messages: Message[]; dismiss: (id: number) => void }) {
  const t = useTranslations("ui.messages");
  const critical = messages.filter((m) => m.priority === "critical");
  const others = messages.filter((m) => m.priority !== "critical");
  return (
    <section className="ui-messages" aria-label={t("region")}>
      <div role="alert" className="ui-messages-critical">
        {critical.map((m) => (
          <MessageItem key={m.id} message={m} dismiss={dismiss} dismissLabel={t("dismiss")} />
        ))}
      </div>
      <div role="status" aria-live="polite" className="ui-messages-other">
        {others.map((m) => (
          <MessageItem key={m.id} message={m} dismiss={dismiss} dismissLabel={t("dismiss")} />
        ))}
      </div>
    </section>
  );
}

function MessageItem({ message, dismiss, dismissLabel }: { message: Message; dismiss: (id: number) => void; dismissLabel: string }) {
  const [paused, setPaused] = useState(false);
  const lifetime = message.action && message.priority !== "critical" ? actionLifetime : lifetimes[message.priority];
  useEffect(() => {
    if (lifetime === null || paused) return;
    const timer = setTimeout(() => dismiss(message.id), lifetime);
    return () => clearTimeout(timer);
  }, [dismiss, lifetime, message.id, paused]);
  return (
    <div
      className="ui-message"
      data-priority={message.priority}
      onPointerEnter={() => setPaused(true)}
      onPointerLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
    >
      <p>{message.text}</p>
      <div className="ui-actions">
        {message.action ? (
          <button
            type="button"
            className="button button-secondary"
            onClick={() => {
              message.action!.run();
              dismiss(message.id);
            }}
          >
            {message.action.label}
          </button>
        ) : null}
        <button type="button" className="button button-quiet" onClick={() => dismiss(message.id)}>
          {dismissLabel}
        </button>
      </div>
    </div>
  );
}
