"use client";

import { useCallback, useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { send, type Outcome } from "@/features/connections/request";
import type { PersonCard } from "@/lib/people/types";
import { personName } from "@/lib/people/types";
import "./admin.css";

export type ActionResult = { ok: boolean; messages: string[] } | null;

/**
 * One change at a time on an admin screen: the button that started it shows it is busy, a step-up answer sends the
 * administrator to a fresh sign-in and back, and the API's own words come back as the result.
 */
export function useAdminAction(returnTo: string) {
  const t = useTranslations("admin.common");
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<ActionResult>(null);

  const run = useCallback(
    async <T,>(key: string, method: string, path: string, body: unknown, success: string | ((data: T) => string)): Promise<Outcome<T>> => {
      setBusy(key);
      setResult(null);
      try {
        const outcome = await send<T>(method, path, body, returnTo, t("failed"));
        if (outcome.ok) {
          setResult({ ok: true, messages: [typeof success === "function" ? success(outcome.data) : success] });
        } else if (!("redirected" in outcome)) {
          setResult({ ok: false, messages: outcome.messages });
        }
        return outcome;
      } finally {
        setBusy(null);
      }
    },
    [returnTo, t],
  );

  return { busy, result, setResult, run };
}

/** The result of the last change, announced once. */
export function ResultAlert({ result }: { result: ActionResult }) {
  const t = useTranslations("admin.common");
  if (!result) return null;
  return (
    <Alert tone={result.ok ? "success" : "danger"} title={result.ok ? t("done") : t("notDone")} live>
      {result.messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </Alert>
  );
}

/**
 * Names for person IDs through the people directory (spec 010), so audit rows, assignments and memberships show who
 * they are about. An ID the directory does not return stays as a short ID.
 */
export function usePeopleNames(ids: readonly (string | null | undefined)[]) {
  const locale = useLocale();
  const [names, setNames] = useState<Record<string, string>>({});
  const wanted = [...new Set(ids.filter((id): id is string => Boolean(id)))].filter((id) => !(id in names));
  const key = wanted.sort().join(",");

  useEffect(() => {
    if (!key) return;
    let cancelled = false;
    void (async () => {
      const found: Record<string, string> = {};
      const list = key.split(",");
      for (let i = 0; i < list.length; i += 50) {
        const chunk = list.slice(i, i + 50);
        try {
          const response = await fetch(`/api/v1/people?ids=${encodeURIComponent(chunk.join(","))}&limit=50`, { cache: "no-store" });
          if (response.ok) {
            const page = (await response.json()) as { items: PersonCard[] };
            for (const p of page.items) found[p.id] = personName(p, locale);
          }
        } catch {
          // Names are a convenience; the short ID stays.
        }
      }
      if (!cancelled) setNames((before) => ({ ...before, ...Object.fromEntries(chunkFallback(list, found)) }));
    })();
    return () => {
      cancelled = true;
    };
  }, [key, locale]);

  return (id: string | null | undefined) => (id ? (names[id] ?? shortId(id)) : "");
}

function chunkFallback(ids: string[], found: Record<string, string>): [string, string][] {
  return ids.map((id) => [id, found[id] ?? shortId(id)]);
}

export function shortId(id: string): string {
  return id.slice(0, 8);
}

/** Date and time in the viewer's language and the tenant's numbering, or the word for never. */
export function useWhen() {
  const format = useFormatter();
  const t = useTranslations("admin.common");
  return (at: string | null | undefined, style: "short" | "medium" = "medium") =>
    at ? format.dateTime(new Date(at), { dateStyle: style, timeStyle: "short" }) : t("never");
}

/** A label from the catalogue when there is one, else the key itself as code. */
export function useKeyLabel(namespace: string) {
  const t = useTranslations(namespace as "admin");
  return (key: string) => (t.has(key as never) ? t(key as never) : key);
}
