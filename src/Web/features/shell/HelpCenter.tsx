"use client";

import { useCallback, useEffect, useId, useRef, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import { createPortal } from "react-dom";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import type { HelpItem } from "@/lib/shell/types";
import { useShellPanel } from "@/lib/shell/panel";

const noSubscription = () => () => {};

/**
 * In-context help and short tours (spec 008 FR-020): what the organization wrote for the page the person is on, from the
 * API in their language. Hints show below the bar until dismissed; a tour shows one step at a time in a panel in the page
 * flow. Neither covers the page or traps focus, and both can be closed. The help menu lists everything for this page and
 * reopens what was dismissed. Dismissals are kept per person by the API.
 */
export function HelpCenter() {
  const t = useTranslations("shell.help");
  const path = usePathname() || "/";
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useShellPanel(root);
  const panelId = useId();
  const [items, setItems] = useState<HelpItem[]>([]);
  const [tour, setTour] = useState<{ key: string; step: number } | null>(null);
  const tourHeading = useRef<HTMLHeadingElement>(null);
  // Hints and the tour sit in the shell's help area, below the bar, once the page is in the browser.
  const slot = useSyncExternalStore(
    noSubscription,
    () => document.querySelector("[data-shell-help]"),
    () => null,
  );

  const load = useCallback(async () => {
    try {
      const response = await fetch(`/api/v1/me/help?path=${encodeURIComponent(path)}`, { cache: "no-store" });
      setItems(response.ok ? ((await response.json()) as HelpItem[]) : []);
    } catch {
      setItems([]);
    }
  }, [path]);

  useEffect(() => {
    const controller = new AbortController();
    fetch(`/api/v1/me/help?path=${encodeURIComponent(path)}`, { cache: "no-store", signal: controller.signal })
      .then(async (response) => (response.ok ? ((await response.json()) as HelpItem[]) : []))
      .then(setItems, () => undefined);
    return () => controller.abort();
  }, [path]);

  useEffect(() => {
    if (tour) tourHeading.current?.focus();
  }, [tour]);

  async function dismiss(key: string, dismissed: boolean) {
    setItems((list) => list.map((i) => (i.key === key ? { ...i, dismissed } : i)));
    await fetch(`/api/v1/me/help/${encodeURIComponent(key)}/dismiss`, { method: dismissed ? "POST" : "DELETE" }).catch(() => undefined);
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && open) {
      setOpen(false);
      button.current?.focus();
    }
  }

  const hints = items.filter((i) => i.kind === "hint" && !i.dismissed);
  const current = tour ? items.find((i) => i.key === tour.key) : undefined;
  const step = current?.steps[tour!.step];

  function closeTour() {
    if (current) void dismiss(current.key, true);
    setTour(null);
    button.current?.focus();
  }

  const area =
    hints.length > 0 || step ? (
      <div className="shell-help-area">
        {hints.map((hint) => (
          <Alert
            key={hint.key}
            tone="info"
            title={hint.title}
            action={
              <Button variant="quiet" size="sm" onClick={() => void dismiss(hint.key, true)}>
                {t("dismiss")}
              </Button>
            }
          >
            <p>{hint.body}</p>
          </Alert>
        ))}
        {current && step ? (
          <section className="shell-tour" aria-labelledby={`${panelId}-tour`}>
            <p className="shell-tour-count">{t("step", { step: tour!.step + 1, count: current.steps.length })}</p>
            <h2 id={`${panelId}-tour`} ref={tourHeading} tabIndex={-1}>
              {step.title}
            </h2>
            <p>{step.body}</p>
            <div className="shell-tour-actions">
              <Button variant="quiet" disabled={tour!.step === 0} onClick={() => setTour({ key: current.key, step: tour!.step - 1 })}>
                {t("back")}
              </Button>
              {tour!.step < current.steps.length - 1 ? (
                <Button variant="primary" onClick={() => setTour({ key: current.key, step: tour!.step + 1 })}>
                  {t("next")}
                </Button>
              ) : (
                <Button variant="primary" onClick={closeTour}>
                  {t("finish")}
                </Button>
              )}
              <Button variant="quiet" onClick={closeTour}>
                {t("close")}
              </Button>
            </div>
          </section>
        ) : null}
      </div>
    ) : null;

  return (
    <div ref={root} className="shell-help" onKeyDown={onKeyDown}>
      <button
        ref={button}
        type="button"
        className="button button-quiet shell-icon-button"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={t("button")}
        title={t("button")}
        onClick={() => {
          setOpen(!open);
          if (!open) void load();
        }}
      >
        <Icon name="info" size={20} />
      </button>
      <div className="shell-help-panel shell-popover" id={panelId} role="dialog" aria-labelledby={`${panelId}-title`} hidden={!open}>
        {open ? (
          <>
            <div className="shell-popover-head">
              <h2 className="shell-popover-title" id={`${panelId}-title`}>
                {t("title")}
              </h2>
            </div>
            <div className="shell-popover-body">
              {items.length === 0 ? (
                <p className="shell-help-none">{t("none")}</p>
              ) : (
                <ul className="shell-favorites-list">
                  {items.map((item) => (
                    <li key={item.key}>
                      <button
                        type="button"
                        className="shell-popover-row"
                        onClick={() => {
                          setOpen(false);
                          if (item.kind === "tour") setTour({ key: item.key, step: 0 });
                          else void dismiss(item.key, false);
                        }}
                      >
                        <Icon name={item.kind === "tour" ? "arrow-forward" : "info"} size={16} />
                        <span className="shell-favorites-text">
                          <bdi>{item.title}</bdi>
                          <small>{item.kind === "tour" ? t("startTour", { count: item.steps.length }) : item.dismissed ? t("showHint") : t("shownHint")}</small>
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </>
        ) : null}
      </div>
      {slot && area ? createPortal(area, slot) : null}
    </div>
  );
}
