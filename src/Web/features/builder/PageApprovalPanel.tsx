"use client";

import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";

/** The approver's decision on a page submission (spec 032 R2 T023): the inbox task's approve or return, then the page reloads. */
export function PageApprovalPanel({ taskId }: { taskId: string }) {
  const t = useTranslations("builder.approval");
  const router = useRouter();
  return <TaskDecisionPanel taskId={taskId} title={t("decide")} onDone={() => router.refresh()} />;
}
