"use client";

import { useEffect, useState } from "react";

/** What an engagement home card has loaded: still loading, failed, off for this viewer (403 or 404), or the answer. */
export type HomeAnswer<T> = "loading" | "failed" | "off" | T;

/**
 * Loads one engagement API answer for a home card (spec 023 T015). A 403 or 404 means the feature is off for this
 * tenant or viewer, which the card shows as empty rather than as an error. An empty `url` loads nothing, for a card
 * that is not set up yet. `retry` loads again after a failure.
 */
export function useHomeAnswer<T>(url: string): { answer: HomeAnswer<T>; retry: () => void } {
  const [answer, setAnswer] = useState<HomeAnswer<T>>("loading");
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!url) return;
    let live = true;
    fetch(url, { cache: "no-store" })
      .then(async (r) => {
        if (r.status === 403 || r.status === 404) {
          if (live) setAnswer("off");
          return;
        }
        if (!r.ok) throw new Error(String(r.status));
        const value = (await r.json()) as T;
        if (live) setAnswer(value);
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [url, attempt]);
  return {
    answer,
    retry: () => {
      setAnswer("loading");
      setAttempt((a) => a + 1);
    },
  };
}

/** The first words of a post for a card: its text blocks joined, cut at about `max` characters on a word. */
export function snippet(body: { blocks: { children?: { text: string }[] | null }[] } | null | undefined, max = 160): string {
  const text = (body?.blocks ?? [])
    .map((b) => (b.children ?? []).map((c) => c.text).join(""))
    .join(" ")
    .replace(/\s+/g, " ")
    .trim();
  if (text.length <= max) return text;
  const cut = text.slice(0, max);
  const space = cut.lastIndexOf(" ");
  return `${(space > max / 2 ? cut.slice(0, space) : cut).trimEnd()}…`;
}
