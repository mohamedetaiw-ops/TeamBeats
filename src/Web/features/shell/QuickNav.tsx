"use client";

import { useEffect, useId, useState } from "react";
import { useTranslations } from "next-intl";
import type { QuickNavItem } from "@/lib/shell/types";
import { Icon } from "@/components/ui/Icon";

/**
 * Quick navigation (spec 008 FR-016): destinations the viewer may open, from the API, as they type. Results are a
 * list of links announced by count; full search (spec 012) replaces the source later.
 */
export function QuickNav() {
  const t = useTranslations("shell.search");
  const [q, setQ] = useState("");
  const [result, setResult] = useState<{ term: string; items: QuickNavItem[] } | null>(null);
  const term = q.trim();
  // Results show only for the current term, so a cleared or short query shows none without extra state.
  const items = term.length >= 2 && result?.term === term ? result.items : null;
  const inputId = useId();
  const listId = useId();

  useEffect(() => {
    if (term.length < 2) {
      return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      fetch(`/api/v1/experience/quick-nav?q=${encodeURIComponent(term)}`, { signal: controller.signal })
        .then((r) => (r.ok ? (r.json() as Promise<QuickNavItem[]>) : []))
        .then((found) => setResult({ term, items: found }))
        .catch(() => undefined);
    }, 200);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [term]);

  return (
    <div className="shell-search" role="search">
      <label htmlFor={inputId} className="visually-hidden">
        {t("label")}
      </label>
      <span className="shell-search-icon">
        <Icon name="search" size={18} />
      </span>
      <input
        id={inputId}
        type="search"
        value={q}
        placeholder={t("placeholder")}
        aria-describedby={items ? listId : undefined}
        onChange={(e) => setQ(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            setQ("");
          }
        }}
      />
      <p className="visually-hidden" aria-live="polite">
        {items ? t("count", { count: items.length }) : ""}
      </p>
      {items && items.length > 0 ? (
        <ul className="shell-search-results" id={listId}>
          {items.map((item) => (
            <li key={item.href}>
              <a href={item.href}>
                <bdi>{item.label}</bdi>
                {item.owner ? <span className="shell-owner">{item.owner}</span> : null}
              </a>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
