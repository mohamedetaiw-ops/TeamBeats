import { problemMessages } from "@/lib/notifications/types";
import { stepUpIfAsked } from "@/lib/security/types";

export type Outcome<T> = { ok: true; data: T } | { ok: false; messages: string[] } | { ok: false; redirected: true; messages: [] };

/**
 * Sends a change to the integrations API. A step-up answer sends the user for a fresh sign-in and back to this page;
 * a refusal returns the API's own localized messages.
 */
export async function send<T>(method: string, path: string, body: unknown, returnTo: string, fallback: string): Promise<Outcome<T>> {
  try {
    const response = await fetch(path, {
      method,
      headers: body === undefined ? undefined : { "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (await stepUpIfAsked(response, returnTo)) return { ok: false, redirected: true, messages: [] };
    if (!response.ok) {
      const messages = await problemMessages(response);
      return { ok: false, messages: messages.length > 0 ? messages : [fallback] };
    }
    return { ok: true, data: (response.status === 204 ? null : await response.json()) as T };
  } catch {
    return { ok: false, messages: [fallback] };
  }
}
