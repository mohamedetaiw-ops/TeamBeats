"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Icon } from "@/components/ui/Icon";
import "./services.css";

/** Read again every 30 seconds while the page is visible, like the notification count (spec 015 section 15). */
export const taskPollInterval = 30_000;

/**
 * The shell's task badge (spec 016 FR-007): how many tasks wait for the signed-in person, linking to the task inbox.
 * The count comes from the API, which applies the assignment, delegation and permission rules.
 */
export function TaskBadge({ initialOpen = null }: { initialOpen?: number | null } = {}) {
  const t = useTranslations("services.badge");
  const [open, setOpen] = useState<number | null>(initialOpen);

  const refresh = useCallback(async () => {
    try {
      const response = await fetch("/api/v1/tasks/count", { cache: "no-store" });
      if (response.ok) setOpen(((await response.json()) as { open: number }).open);
    } catch {
      // Offline: keep the last count; the next poll tries again.
    }
  }, []);

  useEffect(() => {
    const tick = () => {
      if (!document.hidden) void refresh();
    };
    const first = setTimeout(tick, 0);
    const timer = setInterval(tick, taskPollInterval);
    document.addEventListener("visibilitychange", tick);
    return () => {
      clearTimeout(first);
      clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
    };
  }, [refresh]);

  const count = open ?? 0;
  return (
    <Link href="/tasks" className="button button-quiet task-badge" aria-label={t("label", { count })} data-testid="task-badge">
      <Icon name="tasks" />
      {count > 0 ? (
        <span className="task-badge-count" aria-hidden="true">
          {count > 99 ? t("overflow") : count}
        </span>
      ) : null}
    </Link>
  );
}
