"use client";

import { useId, useRef, useState, type KeyboardEvent } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import type { Favorite } from "@/lib/shell/types";
import { useShellPanel } from "@/lib/shell/panel";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * The person's favorites in the shell (spec 008 FR-017, US4 scenario 6): a star button that opens a menu of their
 * favorites, read again from the API each time it opens. One the person can no longer open shows disabled with the
 * reason and no link. On a shell address the page passes as `here`, the menu offers to add or remove it; the API checks
 * that the person may open it. Escape, a press outside or opening another shell panel closes it.
 */
export function FavoritesMenu({ here, label }: { here?: string; label?: string }) {
  const t = useTranslations("shell.favorites");
  const root = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useShellPanel(root);
  const button = useRef<HTMLButtonElement>(null);
  const panelId = useId();
  const [items, setItems] = useState<Favorite[] | "loading" | "failed">("loading");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);

  async function load() {
    setItems("loading");
    try {
      const response = await fetch("/api/v1/me/favorites", { cache: "no-store" });
      setItems(response.ok ? ((await response.json()) as Favorite[]) : "failed");
    } catch {
      setItems("failed");
    }
  }

  const list = Array.isArray(items) ? items : [];
  const current = here ? list.find((f) => f.kind === "page" && f.href === here) : undefined;

  async function toggleHere() {
    if (!here) return;
    setBusy(true);
    setResult(null);
    try {
      const response = current
        ? await fetch(`/api/v1/me/favorites/${encodeURIComponent(current.id)}`, { method: "DELETE" })
        : await fetch("/api/v1/me/favorites", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ path: here, label: (label || document.title || here).slice(0, 200) }),
          });
      if (response.ok) {
        setResult({ ok: true, messages: [current ? t("removed") : t("added")] });
        await load();
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && open) {
      setOpen(false);
      button.current?.focus();
    }
  }

  return (
    <div ref={root} className="shell-favorites" onKeyDown={onKeyDown}>
      <button
        ref={button}
        type="button"
        className="button button-quiet shell-favorites-button"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={t("button")}
        title={t("button")}
        onClick={() => {
          setOpen(!open);
          if (!open) {
            setResult(null);
            void load();
          }
        }}
      >
        <Icon name="star" />
      </button>
      <div className="shell-favorites-panel shell-popover" id={panelId} role="dialog" aria-labelledby={`${panelId}-title`} hidden={!open}>
        {open ? (
          <>
            <div className="shell-popover-head">
              <h2 className="shell-popover-title" id={`${panelId}-title`}>
                {t("title")}
              </h2>
            </div>
            <div className="shell-popover-body">
              {here && Array.isArray(items) ? (
                <>
                  <button type="button" className="shell-popover-row" onClick={() => void toggleHere()} disabled={busy} aria-pressed={current ? true : false}>
                    <Icon name={current ? "minus" : "plus"} size={16} />
                    {current ? t("removeHere") : t("addHere")}
                  </button>
                  <hr className="shell-popover-divider" />
                </>
              ) : null}
              {items === "loading" ? (
                <div className="shell-favorites-skeleton" aria-busy="true">
                  {[0, 1, 2].map((i) => (
                    <span key={i} className="ui-skeleton" />
                  ))}
                </div>
              ) : items === "failed" ? (
                <StateView kind="error" title={t("failed")} message="" />
              ) : items.length === 0 ? (
                <StateView kind="empty" title={t("empty")} message={t("emptyHint")} />
              ) : (
                <ul className="shell-favorites-list">
                  {items.map((f) => (
                    <li key={f.id}>
                      {f.available && f.href ? (
                        <Link className="shell-popover-row" href={f.href}>
                          <Icon name="star" size={16} />
                          <bdi>{f.label}</bdi>
                        </Link>
                      ) : (
                        <span className="shell-popover-row shell-favorites-off" aria-disabled="true">
                          <Icon name="lock" size={16} />
                          <span className="shell-favorites-text">
                            <bdi>{f.label}</bdi>
                            <small>{t(`reasons.${f.reason ?? "not_available"}`)}</small>
                          </span>
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              )}
              {result ? (
                <div className={result.ok ? "field-hint" : "field-error"} role={result.ok ? "status" : "alert"}>
                  {result.messages.map((m) => (
                    <p key={m}>{m}</p>
                  ))}
                </div>
              ) : null}
            </div>
          </>
        ) : null}
      </div>
    </div>
  );
}
