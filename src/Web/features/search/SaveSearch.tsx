"use client";

import { useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import "./search.css";

/**
 * Saves the search on the results page (spec 012 US7) with its daily alert on, so the person hears once a day about new
 * items that match and that they may open. The API keeps the limit and refuses a search saved twice.
 */
export function SaveSearch({ q }: { q: string }) {
  const t = useTranslations("search.save");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);

  async function save() {
    setBusy(true);
    setMessage(null);
    try {
      const response = await fetch("/api/v1/search/saved", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ query: q, alerts: true }),
      });
      if (response.ok) {
        setMessage({ error: false, text: t("saved") });
      } else {
        setMessage({ error: true, text: (await problemMessages(response)).join(" ") || t("failed") });
      }
    } catch {
      setMessage({ error: true, text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="search-save" data-testid="search-save">
      <div className="button-row">
        <Button variant="secondary" size="sm" onClick={() => void save()} disabled={busy}>
          {t("action")}
        </Button>
        <Link href="/search/saved">{t("list")}</Link>
      </div>
      <div aria-live="polite">
        {message ? (
          <p className={message.error ? "field-error" : "search-count"} role={message.error ? "alert" : "status"}>
            {message.text}
          </p>
        ) : null}
      </div>
    </div>
  );
}
