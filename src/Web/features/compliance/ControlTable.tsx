"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { DataTable } from "@/components/ui/DataTable";
import { SelectField } from "@/components/ui/FormField";
import { controlHref, controlStatuses, pick, statusTone, type ControlSummary } from "@/lib/compliance";

/**
 * The controls of a project (FR-022): ID, title, owner, status, due date, tasks and evidence, filtered by status.
 * Each row opens the control, where owners and managers make changes.
 */
export function ControlTable({ controls }: { controls: ControlSummary[] }) {
  const t = useTranslations("compliance");
  const locale = useLocale();
  const format = useFormatter();
  const [status, setStatus] = useState("");
  const rows = useMemo(() => (status ? controls.filter((c) => c.status === status) : controls), [controls, status]);
  return (
    <DataTable<ControlSummary>
      caption={t("controls.title")}
      rows={rows}
      rowKey={(c) => c.id}
      density="compact"
      emptyTitle={t("controls.noneTitle")}
      emptyMessage={t("controls.none")}
      toolbar={
        <SelectField
          label={t("controls.filter")}
          name="status"
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          options={[{ value: "", label: t("controls.all") }, ...controlStatuses.map((s) => ({ value: s, label: t(`status.${s}`) }))]}
        />
      }
      columns={[
        { key: "code", header: t("controls.code"), rowHeader: true, cell: (c) => <bdi dir="ltr">{c.code}</bdi> },
        {
          key: "title",
          header: t("controls.control"),
          sortValue: (c) => pick(c.titles, locale),
          cell: (c) => (
            <Link href={controlHref(c.id)}>
              <bdi>{pick(c.titles, locale)}</bdi>
            </Link>
          ),
        },
        { key: "owner", header: t("controls.owner"), cell: (c) => <bdi>{c.owner?.displayName ?? t("noOwner")}</bdi> },
        {
          key: "status",
          header: t("controls.status"),
          sortValue: (c) => controlStatuses.indexOf(c.status),
          cell: (c) => <Badge tone={statusTone[c.status]}>{t(`status.${c.status}`)}</Badge>,
        },
        {
          key: "due",
          header: t("controls.due"),
          sortValue: (c) => c.dueOn ?? "9999",
          cell: (c) =>
            c.dueOn ? (
              <span>
                {format.dateTime(new Date(`${c.dueOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" })}
                {c.overdue ? (
                  <>
                    {" "}
                    <Badge tone="warning">{t("controls.overdue")}</Badge>
                  </>
                ) : null}
              </span>
            ) : (
              t("controls.noDue")
            ),
        },
        { key: "tasks", header: t("controls.tasks"), align: "end", priority: "low", cell: (c) => t("controls.tasksOpen", { open: c.openTasks, total: c.tasks }) },
        { key: "evidence", header: t("controls.evidence"), align: "end", priority: "low", cell: (c) => format.number(c.evidence) },
      ]}
    />
  );
}
