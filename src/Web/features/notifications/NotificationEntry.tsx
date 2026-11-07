"use client";

import { useLocale, useTranslations } from "next-intl";
import type { NotificationItem } from "@/lib/notifications/types";

/**
 * Marks the notification read and goes to its link. The link is a path on this tenant's host (the API refuses any
 * other), and the destination page checks access itself (spec 015 FR-015).
 */
export async function openNotification(item: NotificationItem) {
  try {
    await fetch(`/api/v1/me/notifications/${item.id}/read`, { method: "POST" });
  } finally {
    window.location.assign(item.link.startsWith("/") && !item.link.startsWith("//") ? item.link : "/");
  }
}

/** One notification: title, body, category, time, and whether it is unread or mandatory. Text is never markup. */
export function NotificationEntry({ item, compact = false, onOpen }: { item: NotificationItem; compact?: boolean; onOpen: (item: NotificationItem) => void }) {
  const t = useTranslations("notifications");
  const locale = useLocale();
  const when = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
  const unread = item.readAt === null;
  return (
    <li className="notification-entry" data-unread={unread || undefined} data-mandatory={item.mandatory || undefined}>
      <a
        href={item.link}
        className="notification-entry-link"
        onClick={(e) => {
          e.preventDefault();
          onOpen(item);
        }}
      >
        <span className="notification-entry-title">
          {unread ? <span className="visually-hidden">{t("unread")}</span> : null}
          <bdi>{item.title}</bdi>
        </span>
        {compact ? null : (
          <span className="notification-entry-body">
            <bdi>{item.body}</bdi>
          </span>
        )}
        <span className="notification-entry-meta">
          {item.mandatory ? <span className="notification-tag">{t("mandatory")}</span> : null}
          <span>{t(`category.${item.category}`)}</span>
          <time dateTime={item.updatedAt}>{when.format(new Date(item.updatedAt))}</time>
        </span>
      </a>
    </li>
  );
}
