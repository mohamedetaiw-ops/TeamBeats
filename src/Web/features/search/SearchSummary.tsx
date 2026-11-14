"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { SearchSummaryView } from "@/lib/search/types";
import "./search.css";

/**
 * The AI summary above search results (spec 012 US6): asked for after the results are on the page, shown only when the
 * assistant wrote one from passages the person may read, with each cited source linked. It is marked as AI generated
 * and can be marked unhelpful, which is kept with its execution record for evaluation (spec 034). When the tenant has
 * summaries off, or nothing permitted answers, nothing is shown and the results stand as usual.
 */
export function SearchSummary({ q }: { q: string }) {
  const t = useTranslations("search.summary");
  const [summary, setSummary] = useState<SearchSummaryView | null>(null);
  const [helpful, setHelpful] = useState<boolean | null>(null);
  const [message, setMessage] = useState<{
    error: boolean;
    text: string;
  } | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(
          `/api/v1/search/summary?${new URLSearchParams({ q })}`,
        );
        if (live && response.ok)
          setSummary((await response.json()) as SearchSummaryView);
      } catch {
        // No summary; the results stand on their own.
      }
    })();
    return () => {
      live = false;
    };
  }, [q]);

  if (
    !summary ||
    summary.state !== "answered" ||
    !summary.text ||
    !summary.turnId
  )
    return null;
  const turnId = summary.turnId;

  async function send(value: boolean) {
    setMessage(null);
    try {
      const response = await fetch(
        `/api/v1/assistant/turns/${encodeURIComponent(turnId)}/feedback`,
        {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ helpful: value, reason: null }),
        },
      );
      if (response.ok) {
        setHelpful(value);
        setMessage({ error: false, text: t("thanks") });
      } else {
        setMessage({
          error: true,
          text: (await problemMessages(response)).join(" ") || t("failed"),
        });
      }
    } catch {
      setMessage({ error: true, text: t("failed") });
    }
  }

  return (
    <section
      className="search-summary"
      aria-labelledby="search-summary-title"
      data-testid="search-summary"
    >
      <h2 id="search-summary-title">{t("title")}</h2>
      <p className="search-summary-text">{summary.text}</p>
      {summary.citations.length > 0 ? (
        <ol className="search-summary-sources" aria-label={t("sources")}>
          {summary.citations.map((c) => (
            <li key={c.n} value={c.n}>
              <Link href={c.link}>
                <bdi>{c.title}</bdi>
              </Link>
            </li>
          ))}
        </ol>
      ) : null}
      <p className="search-count">{t("generated")}</p>
      <div className="button-row">
        <span>{t("question")}</span>
        <Button
          variant="quiet"
          aria-pressed={helpful === true}
          onClick={() => void send(true)}
        >
          {t("helpful")}
        </Button>
        <Button
          variant="quiet"
          aria-pressed={helpful === false}
          onClick={() => void send(false)}
        >
          {t("notHelpful")}
        </Button>
      </div>
      <div aria-live="polite">
        {message ? (
          <p
            className={message.error ? "field-error" : "search-count"}
            role={message.error ? "alert" : "status"}
          >
            {message.text}
          </p>
        ) : null}
      </div>
    </section>
  );
}
