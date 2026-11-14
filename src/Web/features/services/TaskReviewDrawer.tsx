"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Drawer } from "@/components/ui/Dialog";
import { Skeleton } from "@/components/ui/Skeleton";
import { StateView } from "@/components/ui/StateView";
import type { OrgUnit } from "@/lib/experience/builder-types";
import type { RequestDetail } from "@/lib/services/types";
import { RequestDetailView } from "./RequestDetailView";

/**
 * Review a request without leaving the queue (spec 016 FR-020, spec 036 DLS-108): a panel from the end edge with the
 * request's details and the decision. Nothing is decided from the list itself; the decision controls only appear here,
 * under the details the API returned for this person.
 */
export function TaskReviewDrawer({
  task,
  meId,
  units,
  onClose,
  onDecided,
}: {
  task: { taskId: string; requestId: string; title: string } | null;
  meId: string;
  units: OrgUnit[];
  onClose: () => void;
  onDecided: (taskId: string, outcome: string) => void;
}) {
  const t = useTranslations("services.inbox");
  const [loaded, setLoaded] = useState<{ requestId: string; detail: RequestDetail | null } | null>(null);
  const requestId = task?.requestId ?? null;

  useEffect(() => {
    if (!requestId) return;
    let cancelled = false;
    void (async () => {
      let detail: RequestDetail | null = null;
      try {
        const response = await fetch(`/api/v1/requests/${encodeURIComponent(requestId)}`, { cache: "no-store" });
        if (response.ok) detail = (await response.json()) as RequestDetail;
      } catch {
        detail = null;
      }
      if (!cancelled) setLoaded({ requestId, detail });
    })();
    return () => {
      cancelled = true;
    };
  }, [requestId]);

  const current = loaded && loaded.requestId === requestId ? loaded : null;
  return (
    <Drawer open={task !== null} onClose={onClose} title={task?.title ?? t("review")} size="lg">
      {task === null ? null : current === null ? (
        <Skeleton lines={["1.5rem", "7rem", "1.5rem", "9rem"]} />
      ) : current.detail === null ? (
        <StateView kind="error" title={t("reviewFailed")} message="" />
      ) : (
        <RequestDetailView detail={current.detail} meId={meId} units={units} taskId={task.taskId} onDecided={(outcome) => onDecided(task.taskId, outcome)} />
      )}
    </Drawer>
  );
}
