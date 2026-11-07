"use client";

import { useCallback, useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import type { NotificationItem, NotificationPage } from "@/lib/notifications/types";
import { NotificationEntry, openNotification } from "./NotificationEntry";
import "./notifications.css";

/** The unread count is read again every 30 seconds while the page is visible (spec 015 section 15). */
export const pollInterval = 30_000;

/**
 * The shell's notifications region (spec 015 US1, FR-003): a bell with the unread count and a panel with the latest
 * notifications. Every item and count comes from the API for the signed-in user; the browser holds nothing else.
 */
export function NotificationBell() {
  const t = useTranslations("notifications");
  const [unread, setUnread] = useState<number | null>(null);
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState<NotificationItem[] | "loading" | "failed">("loading");
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
      const response = await fetch("/api/v1/me/notifications?limit=8", { cache: "no-store" });
      if (!response.ok) {
        setItems("failed");
        return;
      }
      const page = (await response.json()) as NotificationPage;
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
    <div className="notification-bell" onKeyDown={onKeyDown}>
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
      <div className="notification-panel" id={panelId} hidden={!open}>
        <div className="notification-panel-head">
          <h2 className="notification-panel-title">{t("title")}</h2>
          {count > 0 ? (
            <button type="button" className="button button-quiet" onClick={() => void readAll()}>
              {t("readAll")}
            </button>
          ) : null}
        </div>
        {items === "loading" ? (
          <StateView kind="loading" message="" minBlockSize="6rem" />
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
        ) : items.length === 0 ? (
          <StateView kind="empty" title={t("empty")} message="" />
        ) : (
          <ul className="notification-list">
            {items.map((item) => (
              <NotificationEntry key={item.id} item={item} compact onOpen={(i) => void openNotification(i)} />
            ))}
          </ul>
        )}
        <div className="notification-panel-foot">
          <Link href="/notifications">{t("viewAll")}</Link>
          <Link href="/notifications/preferences">{t("preferences.link")}</Link>
        </div>
      </div>
    </div>
  );
}
