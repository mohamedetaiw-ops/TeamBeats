import { problemMessages } from "@/lib/notifications/types";

export type Sent<T> = { ok: true; data: T } | { ok: false; messages: string[] };

/**
 * A change sent from the browser to the evaluation API through the same-origin proxy. The API checks every call and
 * answers refusals with localized problem messages, which are shown as they are.
 */
export async function send<T>(path: string, method: "POST" | "PUT" | "DELETE", body: unknown, failed: string): Promise<Sent<T>> {
  try {
    const response = await fetch(path, {
      method,
      headers: body === undefined ? undefined : { "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) return { ok: false, messages: await problemMessages(response) };
    const text = await response.text();
    return { ok: true, data: (text.length === 0 ? null : JSON.parse(text)) as T };
  } catch {
    return { ok: false, messages: [failed] };
  }
}
