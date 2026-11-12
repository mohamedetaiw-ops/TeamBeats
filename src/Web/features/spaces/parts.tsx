"use client";

import { useFormatter, useTranslations } from "next-intl";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { SectionNav } from "@/components/ui/SectionNav";
import { spaceDiscussionHref, spaceFilesHref, spaceHref, spaceMembersHref, spaceSettingsHref, spaceTasksHref, type Priority, type SpaceRole, type StatusValue, type Visibility } from "@/lib/spaces";

const visibilityIcons = { public: "globe", private: "lock", secret: "eye" } as const;

export function VisibilityBadge({ visibility }: { visibility: Visibility }) {
  const t = useTranslations("spaces.parts");
  return <Badge icon={visibilityIcons[visibility]}>{t(`visibility.${visibility}`)}</Badge>;
}

export function RoleBadge({ role, guest = false }: { role: SpaceRole | null; guest?: boolean }) {
  const t = useTranslations("spaces.parts");
  if (!role) return null;
  return (
    <>
      <Badge tone={role === "owner" ? "info" : "neutral"}>{t(`role.${role}`)}</Badge>
      {guest ? <Badge tone="warning">{t("guest")}</Badge> : null}
    </>
  );
}

const priorityTones: Record<Priority, BadgeTone> = { low: "neutral", normal: "neutral", high: "warning", urgent: "danger" };

export function PriorityBadge({ priority }: { priority: Priority }) {
  const t = useTranslations("spaces.parts");
  if (priority === "normal") return null;
  return <Badge tone={priorityTones[priority]}>{t(`priority.${priority}`)}</Badge>;
}

const statusTones: Record<StatusValue, BadgeTone> = { on_track: "success", at_risk: "warning", off_track: "danger", done: "info" };

export function StatusBadge({ status }: { status: StatusValue }) {
  const t = useTranslations("spaces.parts");
  return <Badge tone={statusTones[status]}>{t(`status.${status}`)}</Badge>;
}

/** A due date in the viewer's language, marked when it has passed on an open task. */
export function DueBadge({ dueOn, overdue }: { dueOn: string | null; overdue: boolean }) {
  const t = useTranslations("spaces.parts");
  const format = useFormatter();
  if (!dueOn) return null;
  const date = format.dateTime(new Date(`${dueOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  return (
    <Badge tone={overdue ? "danger" : "neutral"} icon="clock">
      {overdue ? t("overdueOn", { date }) : t("dueOn", { date })}
    </Badge>
  );
}

/** The pages of one space. Settings shows only to owners and spaces administrators; the API checks again. */
type SpacePage = "overview" | "tasks" | "files" | "discussion" | "members" | "settings";

/** The space's pages. Files and Discussion appear once the space has its library and discussion (FR-006). */
export function SpaceNav({ id, current, settings, files = false, discussion = false }: { id: string; current: SpacePage; settings: boolean; files?: boolean; discussion?: boolean }) {
  const t = useTranslations("spaces.parts");
  const items = [
    { key: "overview", href: spaceHref(id), label: t("nav.overview") },
    { key: "tasks", href: spaceTasksHref(id), label: t("nav.tasks") },
    ...(files ? [{ key: "files", href: spaceFilesHref(id), label: t("nav.files") }] : []),
    ...(discussion ? [{ key: "discussion", href: spaceDiscussionHref(id), label: t("nav.discussion") }] : []),
    { key: "members", href: spaceMembersHref(id), label: t("nav.members") },
    ...(settings ? [{ key: "settings", href: spaceSettingsHref(id), label: t("nav.settings") }] : []),
  ];
  return <SectionNav label={t("nav.label")} items={items} current={current} />;
}
