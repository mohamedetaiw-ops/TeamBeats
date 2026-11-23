"use client";

import { useState } from "react";
import { problemMessages } from "@/lib/notifications/types";

export type SendResult = { ok: boolean; messages: string[] } | null;

/**
 * One request at a time from a management screen: sends JSON, then reports the API's answer as messages. The screen never
 * decides what is allowed; it shows what the API says.
 */
export function useSend(failed: string) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<SendResult>(null);

  async function send<T = unknown>(url: string, method: string, body: unknown, done: string): Promise<T | null> {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(url, {
        method,
        headers: body === null ? undefined : { "content-type": "application/json" },
        body: body === null ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        setResult({ ok: true, messages: [done] });
        const text = await response.text();
        return (text ? JSON.parse(text) : {}) as T;
      }
      const messages = await problemMessages(response);
      setResult({ ok: false, messages: messages.length > 0 ? messages : [failed] });
    } catch {
      setResult({ ok: false, messages: [failed] });
    } finally {
      setBusy(false);
    }
    return null;
  }

  return { busy, result, send, setResult };
}

export function SendMessages({ result }: { result: SendResult }) {
  return (
    <div aria-live="polite">
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"} data-testid="manage-result">
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}
