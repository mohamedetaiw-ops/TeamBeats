"use client";

import { useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Checkbox } from "@/components/ui/Checkbox";
import { StateView } from "@/components/ui/StateView";
import { call, send, spaceHref, spacesApi, taskHref, type MyTask, type TaskDetail } from "@/lib/spaces";
import { DueBadge, PriorityBadge } from "./parts";
import "./spaces.css";

/**
 * My tasks (spec 029 US7, FR-010): the viewer's open tasks from every space they are in, by due date with the space
 * name, completed in place. It shows as a tab of the task inbox; the list itself comes from the API.
 */
export function MyTasks({ initial }: { initial: MyTask[] }) {
  const t = useTranslations("spaces.mine");
  const [tasks, setTasks] = useState(initial);
  const [messages, setMessages] = useState<string[]>([]);

  /** Shows the change at once and puts the task back if the API refuses it. */
  async function complete(item: MyTask, done: boolean) {
    setTasks((list) => list.map((x) => (x.task.id === item.task.id ? { ...x, task: { ...x.task, status: done ? "done" : "open" } } : x)));
    const result = await call<TaskDetail>(() => send(`${spacesApi}/${item.spaceId}/tasks/${item.task.id}:${done ? "complete" : "reopen"}`, "POST"), t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      setTasks((list) => list.map((x) => (x.task.id === item.task.id ? item : x)));
      return;
    }
    setMessages([]);
    setTasks((list) => list.map((x) => (x.task.id === item.task.id ? { ...x, task: result.value.task } : x)));
  }

  if (tasks.length === 0) return <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="tasks" />;
  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <ul className="spaces-list" aria-label={t("listLabel")}>
        {tasks.map((item) => (
          <li key={item.task.id} className="spaces-row">
            <Checkbox label={t("complete", { title: item.task.title })} checked={item.task.status === "done"} onChange={(e) => void complete(item, e.target.checked)} />
            <Link href={taskHref(item.spaceId, item.task.id)}>
              <bdi>{item.task.title}</bdi>
            </Link>
            <PriorityBadge priority={item.task.priority} />
            <DueBadge dueOn={item.task.dueOn} overdue={item.task.overdue} />
            <Link href={spaceHref(item.spaceId)} className="spaces-meta">
              <bdi>{item.spaceName}</bdi>
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
