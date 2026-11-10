"use client";

import { useEffect, useId, useState } from "react";
import { useTranslations } from "next-intl";
import type { QuickNavItem } from "@/lib/shell/types";
import type { Suggestion } from "@/lib/search/types";
import { CommandPalette, type CommandGroup } from "@/components/ui/CommandPalette";
import { Icon } from "@/components/ui/Icon";

type Found = { term: string; pages: QuickNavItem[]; suggestions: Suggestion[] };

async function json<T>(response: Response): Promise<T[]> {
  return response.ok ? ((await response.json()) as T[]) : [];
}

/**
 * The shell search box (spec 008 FR-016, spec 012 US1 and FR-009). As the person types it lists pages they may open
 * and permitted search suggestions, both from the API; Enter opens the full results page. Results are links announced
 * by count; Escape clears the box. Ctrl+K, or Command+K on a Mac, opens the same search as a command palette
 * (spec 036 DLS-036), so it is one keystroke away on every page.
 */
export function QuickNav() {
  const t = useTranslations("shell.search");
  const [q, setQ] = useState("");
  const [found, setFound] = useState<Found | null>(null);
  const term = q.trim();
  // Results show only for the current term, so a cleared or short query shows none without extra state.
  const current = term.length >= 2 && found?.term === term ? found : null;
  const count = current ? current.pages.length + current.suggestions.length : 0;
  const inputId = useId();
  const listId = useId();
  const [palette, setPalette] = useState(false);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setPalette(true);
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  const groups: CommandGroup[] = [];
  if (palette && current && current.pages.length > 0) {
    groups.push({
      key: "pages",
      label: t("palette.pages"),
      commands: current.pages.map((item) => ({ key: `page:${item.href}`, label: item.label, hint: item.owner ?? undefined, icon: "arrow-forward", href: item.href })),
    });
  }
  if (palette && current && current.suggestions.length > 0) {
    groups.push({
      key: "suggestions",
      label: t("palette.suggestions"),
      commands: current.suggestions.map((item) => ({ key: `${item.source}:${item.itemId}`, label: item.title, hint: t(`kinds.${item.kind}`), href: item.link })),
    });
  }
  if (palette && term.length >= 2) {
    groups.push({
      key: "all",
      label: t("label"),
      commands: [{ key: "all", label: t("all", { term }), icon: "search", href: `/search?q=${encodeURIComponent(term)}` }],
    });
  }

  useEffect(() => {
    if (term.length < 2) {
      return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      const query = encodeURIComponent(term);
      Promise.all([
        fetch(`/api/v1/experience/quick-nav?q=${query}`, { signal: controller.signal }).then((r) => json<QuickNavItem>(r)),
        // Search may be off for the package or unreachable; the pages still show.
        fetch(`/api/v1/search/suggest?q=${query}`, { signal: controller.signal })
          .then((r) => json<Suggestion>(r))
          .catch(() => [] as Suggestion[]),
      ])
        .then(([pages, suggestions]) => setFound({ term, pages, suggestions }))
        .catch(() => undefined);
    }, 200);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [term]);

  return (
    <form className="shell-search" role="search" action="/search" method="get">
      <label htmlFor={inputId} className="visually-hidden">
        {t("label")}
      </label>
      <span className="shell-search-icon">
        <Icon name="search" size={18} />
      </span>
      <input
        id={inputId}
        type="search"
        name="q"
        value={q}
        maxLength={200}
        placeholder={t("placeholder")}
        aria-describedby={current && !palette ? listId : undefined}
        aria-keyshortcuts="Control+K Meta+K"
        onChange={(e) => setQ(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            setQ("");
          }
        }}
      />
      <p className="visually-hidden" aria-live="polite">
        {current ? t("count", { count }) : ""}
      </p>
      {current && count > 0 && !palette ? (
        <ul className="shell-search-results" id={listId}>
          {current.pages.map((item) => (
            <li key={`page:${item.href}`}>
              <a href={item.href}>
                <bdi>{item.label}</bdi>
                {item.owner ? <span className="shell-owner">{item.owner}</span> : null}
              </a>
            </li>
          ))}
          {current.suggestions.map((item) => (
            <li key={`${item.source}:${item.itemId}`}>
              <a href={item.link}>
                <bdi>{item.title}</bdi>
                <span className="shell-owner">{t(`kinds.${item.kind}`)}</span>
              </a>
            </li>
          ))}
          <li>
            <a href={`/search?q=${encodeURIComponent(term)}`}>{t("all", { term })}</a>
          </li>
        </ul>
      ) : null}
      <CommandPalette
        open={palette}
        onClose={() => setPalette(false)}
        label={t("palette.label")}
        placeholder={t("placeholder")}
        empty={t("palette.hint")}
        groups={groups}
        onQueryChange={setQ}
      />
    </form>
  );
}
