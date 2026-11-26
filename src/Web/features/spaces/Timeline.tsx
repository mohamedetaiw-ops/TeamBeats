"use client";

import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { ChartCard, ScheduleChart, type ScheduleRow } from "@/components/ui/Charts";
import { StateView } from "@/components/ui/StateView";
import { taskHref, type TimelineTask, type TimelineView } from "@/lib/spaces";
import "./spaces.css";

/**
 * The plan of a space (spec 029 R3 US8): every dated task as a bar from start to due, grouped by milestone, with its
 * blockers and schedule conflicts, and the milestones on the axis. A bar's dates change on the task's page, with a
 * dates form that works from the keyboard. The axis follows the reading direction.
 */
export function Timeline({ spaceId, view, today }: { spaceId: string; view: TimelineView; today: string }) {
  const t = useTranslations("spaces.timeline");
  const format = useFormatter();
  const day = (iso: string) => format.dateTime(new Date(`${iso}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const titles = new Map([...view.tasks, ...view.undated].map((x) => [x.id, x.title]));

  if (!view.from || !view.to || view.tasks.length === 0) {
    return (
      <>
        <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} />
        <Undated spaceId={spaceId} tasks={view.undated} />
      </>
    );
  }

  function row(task: TimelineTask): ScheduleRow {
    const start = task.startOn ?? task.dueOn!;
    const end = task.dueOn ?? task.startOn!;
    const blockers = view.links.filter((l) => l.taskId === task.id).map((l) => titles.get(l.blockerId) ?? "");
    const parts = [start === end ? day(start) : t("range", { start: day(start), end: day(end) })];
    if (task.status === "done") parts.push(t("done"));
    else if (task.conflict) parts.push(t("conflict"));
    else if (task.overdue) parts.push(t("late"));
    if (blockers.length > 0) parts.push(t("waitsFor", { titles: blockers.join(", ") }));
    return {
      key: task.id,
      label: task.title,
      href: taskHref(spaceId, task.id),
      start,
      end,
      state: task.status === "done" ? "done" : task.conflict ? "conflict" : task.overdue ? "late" : "open",
      detail: parts.join(" · "),
    };
  }

  const groups = [
    ...view.milestones.map((m) => ({ key: m.id, title: m.name, tasks: view.tasks.filter((x) => x.milestoneId === m.id), marker: m })),
    { key: "none", title: t("noMilestone"), tasks: view.tasks.filter((x) => !x.milestoneId || !view.milestones.some((m) => m.id === x.milestoneId)), marker: null },
  ].filter((g) => g.tasks.length > 0);
  const markers = view.milestones.filter((m) => m.dueOn).map((m) => ({ key: m.id, label: m.name, date: m.dueOn! }));
  const conflicts = view.tasks.filter((x) => x.conflict).length;

  return (
    <div className="spaces">
      <p className="spaces-meta">
        {t("summary", { count: view.tasks.length, start: day(view.from), end: day(view.to) })}
        {conflicts > 0 ? (
          <>
            {" "}
            <Badge tone="danger" icon="alert">
              {t("conflicts", { count: conflicts })}
            </Badge>
          </>
        ) : null}
      </p>
      {groups.map((g, i) => {
        const rows = g.tasks.map(row);
        return (
          <ChartCard key={g.key} title={g.title}
            description={g.marker?.dueOn ? t("milestoneDue", { date: day(g.marker.dueOn) }) : undefined}
            table={{ head: [t("task"), t("dates"), t("state")], rows: rows.map((r) => [r.label, r.start === r.end ? day(r.start) : t("range", { start: day(r.start), end: day(r.end) }), r.detail]) }}>
            <ScheduleChart label={t("chartLabel", { group: g.title })} from={view.from!} to={view.to!} rows={rows} markers={i === 0 ? markers : []} today={today} />
          </ChartCard>
        );
      })}
      <Undated spaceId={spaceId} tasks={view.undated} />
    </div>
  );
}

function Undated({ spaceId, tasks }: { spaceId: string; tasks: TimelineTask[] }) {
  const t = useTranslations("spaces.timeline");
  if (tasks.length === 0) return null;
  return (
    <section aria-labelledby="timeline-undated">
      <h2 id="timeline-undated">{t("undatedTitle")}</h2>
      <p className="spaces-meta">{t("undated")}</p>
      <ul className="spaces-list">
        {tasks.map((x) => (
          <li key={x.id}>
            <a href={taskHref(spaceId, x.id)}>
              <bdi>{x.title}</bdi>
            </a>
          </li>
        ))}
      </ul>
    </section>
  );
}
