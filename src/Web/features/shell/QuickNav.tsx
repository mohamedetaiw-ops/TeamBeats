"use client";

import { useEffect, useId, useState } from "react";
import { useTranslations } from "next-intl";
import type { CommandItem, Favorite, QuickNavItem, RecentVisit } from "@/lib/shell/types";
import type { Suggestion } from "@/lib/search/types";
import { CommandPalette, type CommandGroup } from "@/components/ui/CommandPalette";
import { Icon, iconNames, type IconName } from "@/components/ui/Icon";

type Found = { term: string; pages: QuickNavItem[]; suggestions: Suggestion[] };

async function json<T>(response: Response): Promise<T[]> {
  return response.ok ? ((await response.json()) as T[]) : [];
}

/**
 * The shell search box (spec 008 FR-016, spec 012 US1 and FR-009). As the person types it lists pages they may open
 * and permitted search suggestions, both from the API; Enter opens the full results page. Results are links announced
 * by count; Escape clears the box. Ctrl+K, or Command+K on a Mac, opens the same search as a command palette
 * (spec 036 DLS-036), so it is one keystroke away on every page. Before anything is typed the palette lists the
 * person's favorites they may still open and their recent visits, with a command to clear the recents (spec 008 FR-017).
 * It also lists the commands the API says the person may use, such as booking a room (spec 008 FR-008); typing filters them.
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

  const [mine, setMine] = useState<{ favorites: Favorite[]; recents: RecentVisit[]; commands: CommandItem[] } | null>(null);

  useEffect(() => {
    if (!palette) return;
    const controller = new AbortController();
    Promise.all([
      fetch("/api/v1/me/favorites", { signal: controller.signal, cache: "no-store" }).then((r) => json<Favorite>(r)),
      fetch("/api/v1/me/recents", { signal: controller.signal, cache: "no-store" }).then((r) => json<RecentVisit>(r)),
      fetch("/api/v1/experience/commands", { signal: controller.signal, cache: "no-store" }).then((r) => json<CommandItem>(r)),
    ])
      .then(([favorites, recents, commands]) => setMine({ favorites, recents, commands }))
      .catch(() => undefined);
    return () => controller.abort();
  }, [palette]);

  async function clearRecents() {
    const response = await fetch("/api/v1/me/recents", { method: "DELETE" }).catch(() => null);
    if (response?.ok) setMine((m) => (m ? { ...m, recents: [] } : m));
  }

  const groups: CommandGroup[] = [];
  const needle = term.toLocaleLowerCase();
  const commands = palette && mine ? mine.commands.filter((c) => t.has(`commands.${c.key}`) && (term.length < 2 || t(`commands.${c.key}`).toLocaleLowerCase().includes(needle))) : [];
  if (commands.length > 0) {
    groups.push({
      key: "commands",
      label: t("palette.commands"),
      commands: commands.map((c) => ({
        key: `command:${c.key}`,
        label: t(`commands.${c.key}`),
        icon: (iconNames as string[]).includes(c.icon) ? (c.icon as IconName) : "arrow-forward",
        href: c.href,
      })),
    });
  }
  if (palette && term.length < 2 && mine) {
    const open = mine.favorites.filter((f) => f.available && f.href);
    if (open.length > 0) {
      groups.push({
        key: "favorites",
        label: t("palette.favorites"),
        commands: open.map((f) => ({ key: `favorite:${f.id}`, label: f.label, icon: "star", href: f.href! })),
      });
    }
    if (mine.recents.length > 0) {
      groups.push({
        key: "recents",
        label: t("palette.recents"),
        commands: [
          ...mine.recents.map((r) => ({ key: `recent:${r.path}`, label: r.label, hint: r.path, icon: "clock" as const, href: r.path })),
          { key: "recents:clear", label: t("palette.clearRecents"), icon: "trash", onSelect: () => void clearRecents() },
        ],
      });
    }
  }
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

  // The palette sits outside the form: a second text field inside it would stop Enter from submitting the search.
  return (
    <>
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
      </form>
      <CommandPalette
        open={palette}
        onClose={() => setPalette(false)}
        label={t("palette.label")}
        placeholder={t("placeholder")}
        empty={t("palette.hint")}
        groups={groups}
        onQueryChange={setQ}
      />
    </>
  );
}
