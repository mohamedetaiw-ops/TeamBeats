"use client";

import { createContext, useCallback, useContext, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import type { LayoutOperation, LayoutView } from "./types";
import "./personalization.css";

const noSubscription = () => () => {};

/** Changes the user can step back through (spec 008 FR-027). */
export const undoDepth = 20;

type Focus = { placementId: string; regionId: string; index: number };

type Personalization = {
  editing: boolean;
  busy: boolean;
  pageId: string;
  /** Sends one change; resolves true when the API accepted it. */
  change: (op: LayoutOperation, focus?: Focus, announcement?: string) => Promise<boolean>;
  focus: Focus | null;
  clearFocus: () => void;
  regionNumber: (regionId: string) => number;
};

const PersonalizationContext = createContext<Personalization | null>(null);

/** Null outside a personalizable page, so placement tools render nothing there. */
export function usePersonalization() {
  return useContext(PersonalizationContext);
}

/**
 * Personalization mode for one page (spec 008 US2). The user's layout is a list of operations over the published
 * page; every change sends the whole list with the revision it was based on, and the page re-renders from the API, so
 * protection and policy stay decided by the server. Undo steps back through the last lists the server accepted.
 */
export function PersonalizationProvider({
  pageId,
  regions,
  initial,
  canPersonalize,
  children,
}: {
  pageId: string;
  regions: string[];
  initial: { operations: LayoutOperation[]; eTag: string };
  canPersonalize: boolean;
  children: ReactNode;
}) {
  const t = useTranslations("personalization");
  const router = useRouter();
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [operations, setOperations] = useState(initial.operations);
  const [history, setHistory] = useState<LayoutOperation[][]>([]);
  const [focus, setFocus] = useState<Focus | null>(null);
  const [announcement, setAnnouncement] = useState("");
  const eTag = useRef(initial.eTag);
  const toggle = useRef<HTMLButtonElement>(null);
  // The shell keeps a place for page actions next to its settings; the toggle goes there once the page is in the browser.
  const slot = useSyncExternalStore(
    noSubscription,
    () => document.querySelector("[data-shell-page-actions]"),
    () => null,
  );

  const announce = useCallback((text: string) => {
    // Clearing first makes a repeated message announce again.
    setAnnouncement("");
    requestAnimationFrame(() => setAnnouncement(text));
  }, []);

  const reload = useCallback(async () => {
    const response = await fetch(`/api/v1/me/pages/${pageId}/layout`);
    if (response.ok) {
      const view = (await response.json()) as LayoutView;
      eTag.current = view.eTag;
      setOperations(view.operations);
    }
    router.refresh();
  }, [pageId, router]);

  const save = useCallback(
    async (next: LayoutOperation[]): Promise<boolean> => {
      setBusy(true);
      try {
        const response = await fetch(`/api/v1/me/pages/${pageId}/layout`, {
          method: "PUT",
          headers: { "content-type": "application/json", "if-match": eTag.current },
          body: JSON.stringify({ operations: next }),
        });
        if (response.ok) {
          const view = (await response.json()) as LayoutView;
          eTag.current = view.eTag;
          setOperations(view.operations);
          router.refresh();
          return true;
        }
        if (response.status === 409) {
          announce(t("conflict"));
          setHistory([]);
          await reload();
          return false;
        }
        const problem = (await response.json().catch(() => null)) as { detail?: string } | null;
        announce(problem?.detail ?? t("failed"));
        return false;
      } catch {
        announce(t("failed"));
        return false;
      } finally {
        setBusy(false);
      }
    },
    [announce, pageId, reload, router, t],
  );

  const change = useCallback(
    async (op: LayoutOperation, target?: Focus, text?: string) => {
      const next = compact(operations, op);
      const ok = await save(next);
      if (ok) {
        setHistory((h) => [...h, operations].slice(-undoDepth));
        if (target) setFocus(target);
        if (text) announce(text);
      }
      return ok;
    },
    [announce, operations, save],
  );

  async function undo() {
    const previous = history.at(-1);
    if (!previous) return;
    if (await save(previous)) {
      setHistory((h) => h.slice(0, -1));
      announce(t("undone"));
    }
  }

  async function reset() {
    const response = await fetch(`/api/v1/me/pages/${pageId}/layout`, { method: "DELETE" });
    if (!response.ok) {
      announce(t("failed"));
      return;
    }
    setHistory([]);
    await reload();
    announce(t("resetDone"));
  }

  function setMode(on: boolean) {
    setEditing(on);
    announce(on ? t("modeOn") : t("modeOff"));
    if (!on) requestAnimationFrame(() => toggle.current?.focus());
  }

  const value = useMemo<Personalization>(
    () => ({
      editing,
      busy,
      pageId,
      change,
      focus,
      clearFocus: () => setFocus(null),
      regionNumber: (id) => regions.indexOf(id) + 1,
    }),
    [busy, change, editing, focus, pageId, regions],
  );

  return (
    <PersonalizationContext.Provider value={value}>
      <div className="pz-page" data-personalizing={editing || undefined} aria-busy={busy || undefined}>
        {canPersonalize && slot
          ? createPortal(
              <button
                ref={toggle}
                type="button"
                className="button button-quiet shell-icon-button pz-toggle"
                aria-pressed={editing}
                title={t("start")}
                onClick={() => setMode(!editing)}
              >
                <Icon name="edit" size={20} />
                <span className="visually-hidden">{t("start")}</span>
              </button>,
              slot,
            )
          : null}
        {canPersonalize && (editing || !slot) ? (
          <div className="pz-toolbar" role="toolbar" aria-label={t("toolbar")}>
            {editing ? (
              <>
                <Button variant="primary" onClick={() => setMode(false)}>
                  {t("done")}
                </Button>
                <Button variant="secondary" disabled={history.length === 0 || busy} onClick={() => void undo()}>
                  {t("undo")}
                </Button>
                <ConfirmAction label={t("reset")} question={t("resetQuestion")} confirmLabel={t("resetConfirm")} cancelLabel={t("cancel")} onConfirm={reset} />
              </>
            ) : (
              <Button ref={toggle} variant="secondary" aria-pressed={false} onClick={() => setMode(true)}>
                {t("start")}
              </Button>
            )}
          </div>
        ) : null}
        <p role="status" aria-live="polite" className="visually-hidden">
          {announcement}
        </p>
        {children}
      </div>
    </PersonalizationContext.Provider>
  );
}

/**
 * Appends a change, keeping the list short: repeated moves of the same item collapse into one, and removing one of the
 * user's own items drops every change about it, because the result is the same.
 */
export function compact(operations: LayoutOperation[], op: LayoutOperation): LayoutOperation[] {
  if (op.op === "remove" && operations.some((o) => o.op === "add" && o.placementId === op.placementId)) {
    return operations.filter((o) => o.placementId !== op.placementId);
  }
  const last = operations.at(-1);
  if (op.op === "move" && last?.op === "move" && last.placementId === op.placementId) {
    return [...operations.slice(0, -1), op];
  }
  return [...operations, op];
}
