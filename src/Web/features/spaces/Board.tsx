"use client";

import { useState, type DragEvent } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { Menu } from "@/components/ui/Menu";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { call, send, spacesApi, taskHref, type ListView, type TaskBoard, type TaskCard, type TaskDetail } from "@/lib/spaces";
import { DueBadge, PriorityBadge } from "./parts";
import "./spaces.css";

/**
 * Tasks of a space as a list or a board (spec 029 US2). On the board a task is dragged to a column, or moved with its
 * "Move to" menu from the keyboard; a done column completes it. Viewers see the same tasks without the controls, and
 * the API refuses their changes in any case.
 */
export function Board({ spaceId, initial }: { spaceId: string; initial: TaskBoard }) {
  const t = useTranslations("spaces.board");
  const [board, setBoard] = useState(initial);
  const [view, setView] = useState("board");
  const [listId, setListId] = useState(initial.lists[0]?.id ?? "");
  const [title, setTitle] = useState("");
  const [newList, setNewList] = useState("");
  const [dragged, setDragged] = useState<string | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const url = `${spacesApi}/${spaceId}`;
  const list = board.lists.find((l) => l.id === listId) ?? board.lists[0];
  const tasks = board.tasks.filter((task) => task.listId === list?.id);
  const edit = board.canEdit;

  function replace(detail: TaskDetail) {
    setBoard((b) => ({ ...b, tasks: [...b.tasks.filter((x) => x.id !== detail.task.id), detail.task] }));
  }

  async function run(request: () => Promise<Response>) {
    const result = await call<TaskDetail>(request, t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    replace(result.value);
  }

  const move = (task: TaskCard, columnId: string) => run(() => send(`${url}/tasks/${task.id}:move`, "POST", { columnId }));
  /** Shows the change at once and puts the task back if the API refuses it. */
  async function complete(task: TaskCard, done: boolean) {
    setBoard((b) => ({ ...b, tasks: b.tasks.map((x) => (x.id === task.id ? { ...x, status: done ? "done" : "open" } : x)) }));
    const result = await call<TaskDetail>(() => send(`${url}/tasks/${task.id}:${done ? "complete" : "reopen"}`, "POST"), t("failed"));
    if (result.ok) {
      setMessages([]);
      replace(result.value);
    } else {
      setMessages(result.messages);
      setBoard((b) => ({ ...b, tasks: b.tasks.map((x) => (x.id === task.id ? task : x)) }));
    }
  }

  async function create() {
    if (!list) return;
    const result = await call<TaskDetail>(() => send(`${url}/tasks`, "POST", { title, listId: list.id }), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setTitle("");
    replace(result.value);
  }

  async function addList() {
    const result = await call<ListView[]>(() => send(`${url}/lists`, "POST", { name: newList }), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setNewList("");
    setBoard((b) => ({ ...b, lists: result.value }));
    setListId(result.value[result.value.length - 1]?.id ?? listId);
  }

  function drop(event: DragEvent<HTMLElement>, columnId: string) {
    event.preventDefault();
    setOver(null);
    const task = board.tasks.find((x) => x.id === dragged);
    setDragged(null);
    if (task && task.columnId !== columnId) void move(task, columnId);
  }

  if (!list) return <StateView kind="empty" title={t("noListsTitle")} message={t("noLists")} icon="tasks" />;
  const sorted = (columnId: string) => tasks.filter((x) => x.columnId === columnId).sort((a, b) => a.position - b.position);
  const columnName = (id: string) => list.columns.find((c) => c.id === id)?.name ?? "";

  const card = (task: TaskCard) => (
    <div
      key={task.id}
      className="spaces-task"
      data-done={task.status === "done" || undefined}
      draggable={edit && view === "board"}
      onDragStart={() => setDragged(task.id)}
      onDragEnd={() => setDragged(null)}
    >
      <Link href={taskHref(spaceId, task.id)}>
        <bdi>{task.title}</bdi>
      </Link>
      <div className="spaces-row">
        <PriorityBadge priority={task.priority} />
        <DueBadge dueOn={task.dueOn} overdue={task.overdue} />
        {task.checklist.total > 0 ? <span className="spaces-meta">{t("checklist", { done: task.checklist.done, total: task.checklist.total })}</span> : null}
        {task.comments > 0 ? <span className="spaces-meta">{t("comments", { count: task.comments })}</span> : null}
      </div>
      {task.assignees.length > 0 ? <p className="spaces-meta">{task.assignees.map((a) => a.displayName).join(", ")}</p> : null}
      {edit ? (
        <Menu
          label={t("moveTo", { title: task.title })}
          icon="more"
          iconOnly
          size="xs"
          variant="quiet"
          items={list.columns.map((c) => ({ key: c.id, label: c.name, radio: true, checked: c.id === task.columnId, onSelect: () => void move(task, c.id) }))}
        />
      ) : null}
    </div>
  );

  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <div className="spaces-toolbar">
        <SegmentedControl label={t("viewLabel")} name="view" value={view} onChange={setView}
          options={[{ value: "board", label: t("boardView") }, { value: "list", label: t("listView") }]} />
        {board.lists.length > 1 ? (
          <SelectField label={t("list")} name="list" value={list.id} onChange={(e) => setListId(e.target.value)}
            options={board.lists.map((l) => ({ value: l.id, label: l.name }))} />
        ) : null}
        <ButtonLink href={`${url}/tasks.csv`} icon="download" size="sm" download>
          {t("export")}
        </ButtonLink>
      </div>
      {edit ? (
        <form
          className="spaces-toolbar"
          onSubmit={(e) => {
            e.preventDefault();
            void create();
          }}
        >
          <FormField label={t("newTask", { list: list.name })} name="title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} required />
          <Button type="submit" icon="plus">
            {t("add")}
          </Button>
        </form>
      ) : (
        <p className="spaces-meta">{t("readOnly")}</p>
      )}

      {view === "board" ? (
        <div className="spaces-board" aria-label={t("boardLabel", { list: list.name })} role="region">
          {list.columns.map((column) => (
            <section
              key={column.id}
              className="spaces-column"
              aria-labelledby={`column-${column.id}`}
              data-drop={over === column.id || undefined}
              onDragOver={(e) => {
                if (!edit || !dragged) return;
                e.preventDefault();
                setOver(column.id);
              }}
              onDragLeave={() => setOver(null)}
              onDrop={(e) => drop(e, column.id)}
            >
              <h3 id={`column-${column.id}`}>
                {column.name} <span className="spaces-meta">{sorted(column.id).length}</span>
              </h3>
              {sorted(column.id).map(card)}
            </section>
          ))}
        </div>
      ) : tasks.length === 0 ? (
        <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="tasks" />
      ) : (
        <div className="spaces-table-wrap">
          <table className="spaces-table">
            <caption className="spaces-meta">{t("tableCaption", { list: list.name })}</caption>
            <thead>
              <tr>
                <th scope="col">{t("done")}</th>
                <th scope="col">{t("task")}</th>
                <th scope="col">{t("column")}</th>
                <th scope="col">{t("assignees")}</th>
                <th scope="col">{t("due")}</th>
              </tr>
            </thead>
            <tbody>
              {[...tasks].sort((a, b) => (a.dueOn ?? "9999").localeCompare(b.dueOn ?? "9999")).map((task) => (
                <tr key={task.id}>
                  <td>
                    <Checkbox label={t("completeTask", { title: task.title })} checked={task.status === "done"} disabled={!edit}
                      onChange={(e) => void complete(task, e.target.checked)} />
                  </td>
                  <td>
                    <Link href={taskHref(spaceId, task.id)}>
                      <bdi>{task.title}</bdi>
                    </Link>{" "}
                    <PriorityBadge priority={task.priority} />
                  </td>
                  <td>{columnName(task.columnId)}</td>
                  <td>{task.assignees.map((a) => a.displayName).join(", ")}</td>
                  <td>
                    <DueBadge dueOn={task.dueOn} overdue={task.overdue} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {edit ? (
        <form
          className="spaces-toolbar"
          onSubmit={(e) => {
            e.preventDefault();
            void addList();
          }}
        >
          <FormField label={t("newList")} name="newList" value={newList} onChange={(e) => setNewList(e.target.value)} maxLength={80} required />
          <Button type="submit" icon="plus" size="sm">
            {t("addList")}
          </Button>
        </form>
      ) : null}
    </div>
  );
}
