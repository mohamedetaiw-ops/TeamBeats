"use client";

import { useId, useState, type ReactNode } from "react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { Avatar } from "./Avatar";
import { Badge, type BadgeTone } from "./Badge";
import { Button, ButtonLink } from "./Button";
import { Checkbox } from "./Checkbox";
import { Dialog } from "./Dialog";
import { Icon, type IconName } from "./Icon";
import { SegmentedControl } from "./SegmentedControl";
import { StateView } from "./StateView";
import { Divider } from "./Status";
import { Tabs } from "./Tabs";

export type Stat = {
  key: string;
  label: string;
  /** The value as the feature formats it, such as "1,284" or "92%". */
  value: string;
  icon?: IconName;
  /** The change against the previous period as a fraction: 0.12 is up twelve percent. */
  change?: number;
  /** Whether a rise is good news, bad news or neither. It sets the tone; the sign and the words carry the meaning. */
  goodWhen?: "up" | "down" | "neither";
  /** The period compared with, such as "last month". */
  period?: string;
};

/** Stat cards (spec 036 DLS-039): headline numbers with their change against the previous period, as a named list. */
export function StatCards({ label, stats }: { label: string; stats: readonly Stat[] }) {
  const t = useTranslations("ui.data");
  const locale = useLocale();
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 1, signDisplay: "exceptZero" });
  return (
    <ul className="ui-stats" aria-label={label}>
      {stats.map((s) => {
        const direction = s.change === undefined ? null : s.change > 0 ? "up" : s.change < 0 ? "down" : "flat";
        const good = s.goodWhen ?? "up";
        const tone: BadgeTone =
          direction === null || direction === "flat" || good === "neither" ? "neutral" : direction === good ? "success" : "danger";
        return (
          <li key={s.key} className="ui-stat">
            <p className="ui-stat-label">
              {s.icon ? (
                <span className="ui-stat-icon">
                  <Icon name={s.icon} size={18} />
                </span>
              ) : null}
              <span>{s.label}</span>
            </p>
            <p className="ui-stat-value">
              <bdi>{s.value}</bdi>
            </p>
            {s.change !== undefined && s.period ? (
              <p className="ui-stat-change">
                <Badge tone={tone} icon={direction === "up" ? "trend-up" : direction === "down" ? "trend-down" : "minus"}>
                  <bdi>{percent.format(s.change)}</bdi>
                </Badge>
                <span>{t("against", { period: s.period })}</span>
              </p>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}

export type Task = {
  id: string;
  title: string;
  href?: string;
  done: boolean;
  due?: { text: string; tone?: BadgeTone };
  priority?: { text: string; tone?: BadgeTone };
  assignee?: string;
  meta?: string;
};

/**
 * Task list (spec 036 DLS-039): tasks with a box to mark each done, the due date and priority in words and the person
 * it is assigned to. Marking done is the feature's call; the list only reports the change.
 */
export function TaskList({
  label,
  tasks,
  onToggle,
  emptyTitle,
  emptyMessage,
}: {
  label: string;
  tasks: readonly Task[];
  onToggle: (id: string, done: boolean) => void;
  emptyTitle?: string;
  emptyMessage?: string;
}) {
  const t = useTranslations("ui.data");
  if (tasks.length === 0) return <StateView kind="empty" title={emptyTitle} message={emptyMessage} />;
  return (
    <ul className="ui-tasks" aria-label={label}>
      {tasks.map((task) => (
        <li key={task.id} className="ui-task" data-done={task.done || undefined}>
          <Checkbox
            label={<span className="visually-hidden">{t(task.done ? "markNotDone" : "markDone", { title: task.title })}</span>}
            checked={task.done}
            onChange={(e) => onToggle(task.id, e.currentTarget.checked)}
          />
          <div className="ui-task-text">
            {task.href ? (
              <Link className="ui-task-title" href={task.href}>
                <bdi>{task.title}</bdi>
              </Link>
            ) : (
              <span className="ui-task-title">
                <bdi>{task.title}</bdi>
              </span>
            )}
            {task.due || task.priority || task.meta ? (
              <span className="ui-task-meta">
                {task.due ? (
                  <Badge tone={task.due.tone} icon="clock">
                    {task.due.text}
                  </Badge>
                ) : null}
                {task.priority ? <Badge tone={task.priority.tone}>{task.priority.text}</Badge> : null}
                {task.meta ? <span>{task.meta}</span> : null}
              </span>
            ) : null}
          </div>
          {task.assignee ? <Avatar name={task.assignee} size="sm" /> : null}
        </li>
      ))}
    </ul>
  );
}

export type Notice = {
  id: string;
  title: string;
  body?: string;
  /** When it happened, as text, such as "10 minutes ago", and as a machine time. */
  time: string;
  dateTime: string;
  unread: boolean;
  icon?: IconName;
  actor?: string;
  href?: string;
};

/**
 * Notification panel (spec 036 DLS-039): the latest notifications with a filter for unread ones and one action to
 * mark them all read. Unread is said in words, not only by the dot. Sits in a popover or on its own page.
 */
export function NotificationPanel({
  title,
  notices,
  onMarkAllRead,
  allHref,
  allLabel,
}: {
  title: string;
  notices: readonly Notice[];
  onMarkAllRead: () => void;
  allHref?: string;
  allLabel?: string;
}) {
  const t = useTranslations("ui.data");
  const id = useId();
  const [filter, setFilter] = useState("all");
  const unread = notices.filter((n) => n.unread).length;
  const shown = filter === "unread" ? notices.filter((n) => n.unread) : notices;
  return (
    <section className="ui-notices" aria-labelledby={`${id}-title`}>
      <header className="ui-notices-head">
        <h3 id={`${id}-title`}>{title}</h3>
        <Button variant="quiet" size="sm" icon="check" onClick={onMarkAllRead} disabled={unread === 0}>
          {t("markAllRead")}
        </Button>
      </header>
      <SegmentedControl
        label={t("show")}
        name={`${id}-filter`}
        value={filter}
        onChange={setFilter}
        options={[
          { value: "all", label: t("all") },
          { value: "unread", label: t("unreadCount", { count: unread }) },
        ]}
      />
      {shown.length === 0 ? (
        <StateView kind="empty" title={t("noUnread")} message="" />
      ) : (
        <ul className="ui-notice-list">
          {shown.map((n) => (
            <li key={n.id} className="ui-notice" data-unread={n.unread || undefined}>
              {n.actor ? (
                <Avatar name={n.actor} size="sm" />
              ) : (
                <span className="ui-notice-icon">
                  <Icon name={n.icon ?? "bell"} size={18} />
                </span>
              )}
              <div className="ui-notice-text">
                {n.href ? (
                  <Link className="ui-notice-title" href={n.href}>
                    <bdi>{n.title}</bdi>
                  </Link>
                ) : (
                  <span className="ui-notice-title">
                    <bdi>{n.title}</bdi>
                  </span>
                )}
                {n.body ? <span className="ui-notice-body">{n.body}</span> : null}
                <time dateTime={n.dateTime}>{n.time}</time>
              </div>
              {n.unread ? (
                <span className="ui-notice-dot">
                  <span className="visually-hidden">{t("unread")}</span>
                </span>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {allHref && allLabel ? (
        <footer className="ui-notices-foot">
          <Link href={allHref}>{allLabel}</Link>
        </footer>
      ) : null}
    </section>
  );
}

/**
 * Settings dialog (spec 036 DLS-039): a large dialog with its sections as vertical tabs at the start. Each section is
 * the feature's own form; the dialog only arranges them. Saving stays inside each section.
 */
export function SettingsDialog({
  open,
  onClose,
  title,
  description,
  sections,
  footer,
}: {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: string;
  sections: { key: string; label: string; icon?: IconName; content: ReactNode }[];
  footer?: ReactNode;
}) {
  const t = useTranslations("ui.data");
  return (
    <Dialog open={open} onClose={onClose} title={title} description={description} size="lg" footer={footer}>
      <div className="ui-settings">
        <Tabs label={t("sections")} tabs={sections} orientation="vertical" />
      </div>
    </Dialog>
  );
}

/**
 * Sign-in card (spec 036 DLS-039): the tenant's sign-in choices on one card. Providers are links to the identity
 * service; the form, when the tenant allows one, is the feature's. Errors are stated above the choices.
 */
export function SignInCard({
  title,
  intro,
  mark,
  providers,
  dividerLabel,
  error,
  children,
  footer,
}: {
  title: string;
  intro?: string;
  /** The tenant's logo, with its alt text. */
  mark?: ReactNode;
  providers: readonly { key: string; label: string; href: string; icon?: IconName }[];
  dividerLabel?: string;
  error?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
}) {
  const id = useId();
  return (
    <section className="ui-signin" aria-labelledby={`${id}-title`}>
      {mark ? <div className="ui-signin-mark">{mark}</div> : null}
      <h2 id={`${id}-title`} className="ui-signin-title">
        {title}
      </h2>
      {intro ? <p className="ui-signin-intro">{intro}</p> : null}
      {error}
      <div className="ui-signin-providers">
        {providers.map((p) => (
          <ButtonLink key={p.key} href={p.href} icon={p.icon} variant={providers.length === 1 ? "primary" : "secondary"}>
            {p.label}
          </ButtonLink>
        ))}
      </div>
      {children ? (
        <>
          {dividerLabel ? <Divider label={dividerLabel} /> : null}
          {children}
        </>
      ) : null}
      {footer ? <div className="ui-signin-foot">{footer}</div> : null}
    </section>
  );
}
