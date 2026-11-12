"use client";

import { useLocale, useTranslations } from "next-intl";
import { Icon, type IconName } from "@/components/ui/Icon";
import { relativeTime, dateTimeFull } from "@/lib/format";
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

/** Categories that ask the reader to do something; the inbox groups them first (spec 036 DLS-046). */
export const actionCategories: ReadonlySet<string> = new Set(["approval_waiting", "approval_escalation", "attestation_due"]);

/** True when the notification waits on the reader: an action category or a required notice, still unread. */
export function needsAction(item: NotificationItem): boolean {
  return item.readAt === null && (item.mandatory || actionCategories.has(item.category));
}

/** The icon tile tells what kind of notification it is (DLS-060); the tone only marks urgency. */
const look: Record<string, { icon: IconName; tone?: "accent" | "danger" | "warning" }> = {
  security_notice: { icon: "lock", tone: "danger" },
  critical_announcement: { icon: "megaphone", tone: "danger" },
  site_emergency: { icon: "alert", tone: "danger" },
  attestation_due: { icon: "policy", tone: "warning" },
  approval_escalation: { icon: "alert", tone: "warning" },
  approval_waiting: { icon: "tasks", tone: "accent" },
  request_update: { icon: "inbox" },
  mention: { icon: "chat", tone: "accent" },
  comment: { icon: "chat" },
  announcement: { icon: "megaphone" },
  document_update: { icon: "files" },
  recognition: { icon: "star" },
  integration_alert: { icon: "link", tone: "warning" },
  system: { icon: "info" },
};

/**
 * One notification row: a category icon tile, the title clamped to two lines, a meta line with the category and a
 * relative time, and an unread dot (DLS-046). The whole row opens the item; an unread row offers Mark as read on
 * hover and focus. Text is never markup.
 */
export function NotificationEntry({
  item,
  compact = false,
  now,
  onOpen,
  onRead,
}: {
  item: NotificationItem;
  compact?: boolean;
  now: number;
  onOpen: (item: NotificationItem) => void;
  onRead?: (item: NotificationItem) => void;
}) {
  const t = useTranslations("notifications");
  const locale = useLocale();
  const unread = item.readAt === null;
  const kind = look[item.category] ?? { icon: "bell" as IconName };
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
        <span className="notification-entry-icon" data-tone={kind.tone} aria-hidden="true">
          <Icon name={kind.icon} size={18} />
        </span>
        <span className="notification-entry-text">
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
            {item.mandatory ? <span className="ui-badge notification-tag" data-tone="warning">{t("mandatory")}</span> : null}
            <span>{t(`category.${item.category}`)}</span>
            <span aria-hidden="true">·</span>
            <time dateTime={item.updatedAt} title={dateTimeFull(item.updatedAt, locale)}>
              {relativeTime(item.updatedAt, locale, now)}
            </time>
          </span>
        </span>
        {unread ? <span className="notification-dot" aria-hidden="true" /> : null}
      </a>
      {unread && onRead ? (
        <button type="button" className="notification-entry-action" onClick={() => onRead(item)}>
          <Icon name="check" size={14} />
          <span>{t("markRead")}</span>
        </button>
      ) : null}
    </li>
  );
}
