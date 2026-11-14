"use client";

import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import type { NotificationFilter, NotificationItem, NotificationPage } from "@/lib/notifications/types";
import { NotificationEntry, openNotification } from "./NotificationEntry";
import "./notifications.css";

const filters: NotificationFilter[] = ["all", "unread", "mandatory"];

/** The full notification centre (spec 015 US1): filters, paging with a cursor, and mark all as read. */
export function NotificationCentre() {
  const t = useTranslations("notifications");
  const [filter, setFilter] = useState<NotificationFilter>("all");
  const [items, setItems] = useState<NotificationItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [unread, setUnread] = useState(0);
  const [state, setState] = useState<"loading" | "ready" | "failed">("loading");
  const [more, setMore] = useState(false);
  const [now, setNow] = useState(0);

  const load = useCallback(async (chosen: NotificationFilter, after: string | null) => {
    try {
      const query = new URLSearchParams({ filter: chosen, limit: "20" });
      if (after) query.set("cursor", after);
      const response = await fetch(`/api/v1/me/notifications?${query}`, { cache: "no-store" });
      if (!response.ok) {
        setState("failed");
        return;
      }
      const page = (await response.json()) as NotificationPage;
      setItems((current) => (after ? [...current, ...page.items] : page.items));
      setCursor(page.nextCursor);
      setNow(Date.now());
      setUnread(page.unread);
      setState("ready");
    } catch {
      setState("failed");
    } finally {
      setMore(false);
    }
  }, []);

  useEffect(() => {
    const start = setTimeout(() => void load(filter, null), 0);
    return () => clearTimeout(start);
  }, [filter, load]);

  async function readAll() {
    await fetch("/api/v1/me/notifications/read-all", { method: "POST" });
    await load(filter, null);
  }

  return (
    <div className="notification-centre">
      <div className="notification-toolbar">
        <div role="group" aria-label={t("filter.label")} className="ui-tablist" data-size="sm">
          {filters.map((f) => (
            <button
              key={f}
              type="button"
              className="ui-tab"
              aria-pressed={f === filter}
              onClick={() => {
                setState("loading");
                setFilter(f);
              }}
            >
              {t(`filter.${f}`)}
            </button>
          ))}
        </div>
        {unread > 0 ? (
          <Button variant="secondary" onClick={() => void readAll()}>
            {t("readAll")}
          </Button>
        ) : null}
      </div>
      {state === "loading" ? (
        <StateView kind="loading" message="" minBlockSize="12rem" />
      ) : state === "failed" ? (
        <StateView
          kind="error"
          title={t("failed")}
          message=""
          action={
            <Button variant="secondary" onClick={() => void load(filter, null)}>
              {t("retry")}
            </Button>
          }
        />
      ) : items.length === 0 ? (
        <StateView kind="empty" title={t(filter === "all" ? "empty" : "emptyFiltered")} message="" />
      ) : (
        <>
          <ul className="notification-list notification-list-full">
            {items.map((item) => (
              <NotificationEntry key={item.id} item={item} now={now} onOpen={(i) => void openNotification(i)} />
            ))}
          </ul>
          {cursor ? (
            <Button
              variant="secondary"
              busy={more}
              busyLabel={t("loading")}
              onClick={() => {
                setMore(true);
                void load(filter, cursor);
              }}
            >
              {t("more")}
            </Button>
          ) : null}
        </>
      )}
    </div>
  );
}
