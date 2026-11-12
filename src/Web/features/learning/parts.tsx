"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import type { OrgUnitView } from "@/lib/admin/types";
import type { AssignmentSource, LearnerStateKind } from "@/lib/learning";

const tones: Record<LearnerStateKind, BadgeTone> = { none: "neutral", open: "info", completed: "success", retraining: "warning" };

/** Where the viewer stands on a course, as a badge. */
export function StateBadge({ state }: { state: LearnerStateKind }) {
  const t = useTranslations("learning");
  return (
    <Badge tone={tones[state]} icon={state === "completed" ? "check" : undefined}>
      {t(`state.${state}`)}
    </Badge>
  );
}

/** Why the viewer has a course: chosen, assigned by a rule or a manager, or given by onboarding or a policy. */
export function SourceText({ source }: { source: AssignmentSource }) {
  const t = useTranslations("learning");
  return <>{t(`source.${source}`)}</>;
}

/** A due date written as a day in the viewer's language, and whether it has passed. */
export function useDue(): (dueOn: string | null) => { text: string; overdue: boolean } | null {
  const format = useFormatter();
  const t = useTranslations("learning");
  return (dueOn) => {
    if (!dueOn) return null;
    const day = new Date(`${dueOn}T12:00:00Z`);
    const overdue = dueOn < new Date().toISOString().slice(0, 10);
    const date = format.dateTime(day, { dateStyle: "medium", timeZone: "UTC" });
    return { text: overdue ? t("overdueOn", { date }) : t("dueOn", { date }), overdue };
  };
}

/** The org units the viewer may see, active ones only, for a course or path scope and audience. */
export function useUnits(): OrgUnitView[] {
  const [units, setUnits] = useState<OrgUnitView[]>([]);
  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/org/units", { cache: "no-store" });
        if (live && response.ok) setUnits(((await response.json()) as OrgUnitView[]).filter((u) => u.status === "active"));
      } catch {
        // Without the unit list the course stays for the whole organization.
      }
    })();
    return () => {
      live = false;
    };
  }, []);
  return units;
}

export const toggle = (list: string[], value: string, on: boolean) => (on ? [...new Set([...list, value])] : list.filter((v) => v !== value));
