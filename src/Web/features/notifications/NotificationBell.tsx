"use client";

import { useCallback, useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import type { NotificationItem, NotificationPage } from "@/lib/notifications/types";
import { useShellPanel } from "@/lib/shell/panel";
import { NotificationEntry, needsAction, openNotification } from "./NotificationEntry";
import "./notifications.css";

/** The unread count is read again every 30 seconds while the page is visible (spec 015 section 15). */
export const pollInterval = 30_000;

type Tab = "all" | "unread" | "action";
const tabs: Tab[] = ["all", "unread", "action"];

/** Midnight today in the reader's time, for the Today and Earlier groups. */
function startOfToday(now: number): number {
  const d = new Date(now);
  d.setHours(0, 0, 0, 0);
  return d.getTime();
}

/**
 * The shell's notifications region (spec 015 US1, FR-003): a bell with the unread count and an inbox popover with the
 * latest notifications (spec 036 DLS-046). The popover has a fixed header with the tabs All, Unread and Needs action, a
 * scrolling body grouped into Requires action, Today and Earlier, and a fixed footer. Every item and count comes from
 * the API for the signed-in user; the tabs only choose which of those the panel shows.
 */
export function NotificationBell() {
  const t = useTranslations("notifications");
  const [unread, setUnread] = useState<number | null>(null);
  const root = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useShellPanel(root);
  const [items, setItems] = useState<NotificationItem[] | "loading" | "failed">("loading");
  const [tab, setTab] = useState<Tab>("all");
  const [now, setNow] = useState(0);
  const panelId = useId();
  const button = useRef<HTMLButtonElement>(null);

  const refreshCount = useCallback(async () => {
    try {
      const response = await fetch("/api/v1/me/notifications/count", { cache: "no-store" });
      if (response.ok) setUnread(((await response.json()) as { unread: number }).unread);
    } catch {
      // Offline: keep the last count; the next poll tries again.
    }
  }, []);

  useEffect(() => {
    const tick = () => {
      if (!document.hidden) void refreshCount();
    };
    const first = setTimeout(tick, 0);
    const timer = setInterval(tick, pollInterval);
    document.addEventListener("visibilitychange", tick);
    return () => {
      clearTimeout(first);
      clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
    };
  }, [refreshCount]);

  async function loadItems() {
    setItems("loading");
    try {
      const response = await fetch("/api/v1/me/notifications?limit=20", { cache: "no-store" });
      if (!response.ok) {
        setItems("failed");
        return;
      }
      const page = (await response.json()) as NotificationPage;
      setNow(Date.now());
      setItems(page.items);
      setUnread(page.unread);
    } catch {
      setItems("failed");
    }
  }

  function toggle() {
    const next = !open;
    setOpen(next);
    if (next) void loadItems();
  }

  async function readOne(item: NotificationItem) {
    await fetch(`/api/v1/me/notifications/${item.id}/read`, { method: "POST" });
    await loadItems();
  }

  async function readAll() {
    await fetch("/api/v1/me/notifications/read-all", { method: "POST" });
    await loadItems();
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && open) {
      setOpen(false);
      button.current?.focus();
    }
  }

  const count = unread ?? 0;
  return (
    <div ref={root} className="notification-bell" onKeyDown={onKeyDown}>
      <button
        ref={button}
        type="button"
        className="button button-quiet notification-bell-button"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={t("bell", { count })}
        onClick={toggle}
      >
        <Icon name="bell" />
        {count > 0 ? (
          <span className="notification-count" aria-hidden="true">
            {count > 99 ? t("overflow") : count}
          </span>
        ) : null}
      </button>
      <span className="visually-hidden" role="status" aria-live="polite">
        {unread === null ? "" : t("unreadStatus", { count })}
      </span>
      <div className="notification-panel shell-popover" id={panelId} role="dialog" aria-labelledby={`${panelId}-title`} hidden={!open}>
        <div className="shell-popover-head">
          <div className="notification-panel-head">
            <h2 className="notification-panel-title" id={`${panelId}-title`}>
              {t("title")}
            </h2>
            {count > 0 ? (
              <button type="button" className="shell-popover-link" onClick={() => void readAll()}>
                {t("readAll")}
              </button>
            ) : null}
          </div>
          <div role="group" aria-label={t("tabs.label")} className="notification-tabs">
            {tabs.map((key) => (
              <button key={key} type="button" className="notification-tab" aria-pressed={tab === key} onClick={() => setTab(key)}>
                {t(`tabs.${key}`)}
              </button>
            ))}
          </div>
        </div>
        <div className="shell-popover-body">
          {items === "loading" ? (
            <div className="notification-skeleton" aria-busy="true">
              {[0, 1, 2].map((i) => (
                <span key={i} className="ui-skeleton" />
              ))}
            </div>
          ) : items === "failed" ? (
            <StateView
              kind="error"
              title={t("failed")}
              message=""
              action={
                <button type="button" className="button button-secondary" onClick={() => void loadItems()}>
                  {t("retry")}
                </button>
              }
            />
          ) : (
            <Groups items={items} tab={tab} now={now} onRead={(i) => void readOne(i)} />
          )}
        </div>
        <div className="shell-popover-foot notification-panel-foot">
          <Link href="/notifications" className="shell-popover-anchor">
            {t("viewAll")}
          </Link>
          <Link href="/notifications/preferences" className="shell-popover-anchor shell-popover-quiet">
            <Icon name="settings" size={14} />
            {t("preferences.link")}
          </Link>
        </div>
      </div>
    </div>
  );
}

/** The rows of the chosen tab, in the groups Requires action, Today and Earlier; an empty group is left out. */
function Groups({ items, tab, now, onRead }: { items: NotificationItem[]; tab: Tab; now: number; onRead: (item: NotificationItem) => void }) {
  const t = useTranslations("notifications");
  const shown = items.filter((i) => (tab === "unread" ? i.readAt === null : tab === "action" ? needsAction(i) : true));
  if (shown.length === 0) {
    return <StateView kind="empty" icon={tab === "all" ? "bell" : "check"} title={t(tab === "all" ? "empty" : tab === "unread" ? "emptyUnread" : "emptyAction")} message="" />;
  }
  const today = startOfToday(now);
  const action = shown.filter(needsAction);
  const rest = shown.filter((i) => !needsAction(i));
  const groups: { key: string; rows: NotificationItem[] }[] = [
    { key: "action", rows: action },
    { key: "today", rows: rest.filter((i) => Date.parse(i.updatedAt) >= today) },
    { key: "earlier", rows: rest.filter((i) => Date.parse(i.updatedAt) < today) },
  ].filter((g) => g.rows.length > 0);
  return (
    <>
      {groups.map((g) => (
        <section key={g.key} className="notification-group" aria-label={t(`groups.${g.key}`)}>
          <h3 className="notification-group-title">{t(`groups.${g.key}`)}</h3>
          <ul className="notification-list">
            {g.rows.map((item) => (
              <NotificationEntry key={item.id} item={item} compact now={now} onOpen={(i) => void openNotification(i)} onRead={onRead} />
            ))}
          </ul>
        </section>
      ))}
    </>
  );
}
