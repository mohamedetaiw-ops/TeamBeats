"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { IconButton } from "@/components/ui/IconButton";
import { Progress } from "@/components/ui/Status";
import { documentHref } from "@/lib/documents/types";
import {
  call,
  priorities,
  send,
  spaceTasksHref,
  spacesApi,
  taskBody,
  type MilestoneView,
  type SpaceMember,
  type TaskDetail,
} from "@/lib/spaces";
import { DueBadge, PriorityBadge } from "./parts";
import "./spaces.css";

/**
 * One task (spec 029 US2, FR-004): what it is, who holds it, when it is due, its checklist and its comments. Members
 * change it; a viewer reads it. Assignees can be the space's owners and members only, which the API checks.
 */
export function TaskView({
  spaceId,
  initial,
  members,
  milestones,
  libraryFiles = [],
}: {
  spaceId: string;
  initial: TaskDetail;
  members: SpaceMember[];
  milestones: MilestoneView[];
  /** Files of the space library a member may link (US5 scenario 3). */
  libraryFiles?: { id: string; name: string }[];
}) {
  const t = useTranslations("spaces.task");
  const format = useFormatter();
  const router = useRouter();
  const [detail, setDetail] = useState(initial);
  const [title, setTitle] = useState(initial.task.title);
  const [description, setDescription] = useState(initial.description);
  const [priority, setPriority] = useState<string>(initial.task.priority);
  const [dueOn, setDueOn] = useState(initial.task.dueOn ?? "");
  const [labels, setLabels] = useState(initial.task.labels.join(", "));
  const [assignees, setAssignees] = useState<string[]>(initial.task.assignees.map((a) => a.userId));
  const [milestoneId, setMilestoneId] = useState(initial.task.milestoneId ?? "");
  const [item, setItem] = useState("");
  const [comment, setComment] = useState("");
  const [file, setFile] = useState("");
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const url = `${spacesApi}/${spaceId}/tasks/${initial.task.id}`;
  const edit = detail.canEdit;
  const workers = members.filter((m) => m.role !== "viewer");

  async function run(request: () => Promise<Response>, after?: () => void) {
    const result = await call<TaskDetail>(request, t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return false;
    }
    setMessages([]);
    setDetail(result.value);
    after?.();
    return true;
  }

  async function save() {
    setBusy(true);
    setSaved(false);
    const body = {
      ...taskBody(detail.task, description),
      title,
      priority,
      dueOn: dueOn || null,
      labels: labels.split(",").map((l) => l.trim()).filter(Boolean),
      assignees,
      milestoneId: milestoneId || null,
    };
    const ok = await run(() => send(url, "PUT", body));
    setBusy(false);
    setSaved(ok);
  }

  async function remove() {
    const result = await call<void>(() => send(url, "DELETE"), t("failed"));
    if (result.ok) router.push(spaceTasksHref(spaceId));
    else setMessages(result.messages);
  }

  const done = detail.task.status === "done";
  const checklist = detail.checklist;

  return (
    <div className="spaces-split">
      <div className="spaces">
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("saved")} live /> : null}
        <div className="spaces-row">
          <PriorityBadge priority={detail.task.priority} />
          <DueBadge dueOn={detail.task.dueOn} overdue={detail.task.overdue} />
          {edit ? (
            <Button variant={done ? "secondary" : "primary"} icon={done ? undefined : "check"}
              onClick={() => void run(() => send(`${url}:${done ? "reopen" : "complete"}`, "POST"))}>
              {done ? t("reopen") : t("complete")}
            </Button>
          ) : null}
        </div>

        {edit ? (
          <form
            className="spaces-form"
            onSubmit={(e) => {
              e.preventDefault();
              void save();
            }}
          >
            <FormField label={t("title")} name="title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} required />
            <TextAreaField label={t("description")} name="description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={10000} />
            <div className="spaces-toolbar">
              <SelectField label={t("priority")} name="priority" value={priority} onChange={(e) => setPriority(e.target.value)}
                options={priorities.map((p) => ({ value: p, label: t(`priorityOption.${p}`) }))} />
              <FormField label={t("dueOn")} name="dueOn" type="date" value={dueOn} onChange={(e) => setDueOn(e.target.value)} />
              <SelectField label={t("milestone")} name="milestone" value={milestoneId} onChange={(e) => setMilestoneId(e.target.value)}
                options={[{ value: "", label: t("noMilestone") }, ...milestones.map((m) => ({ value: m.id, label: m.name }))]} />
            </div>
            <FormField label={t("labels")} hint={t("labelsHint")} name="labels" value={labels} onChange={(e) => setLabels(e.target.value)} />
            <fieldset>
              <legend>{t("assignees")}</legend>
              {workers.length === 0 ? <p className="spaces-meta">{t("noWorkers")}</p> : null}
              {workers.map((m) => (
                <Checkbox key={m.userId} label={m.displayName} checked={assignees.includes(m.userId)}
                  onChange={(e) => setAssignees((list) => (e.target.checked ? [...new Set([...list, m.userId])] : list.filter((id) => id !== m.userId)))} />
              ))}
            </fieldset>
            <div className="spaces-row">
              <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
                {t("save")}
              </Button>
              <ConfirmAction label={t("delete")} question={t("deleteQuestion")} confirmLabel={t("deleteConfirm")} cancelLabel={t("cancel")} onConfirm={remove} />
            </div>
          </form>
        ) : (
          <>
            {detail.description ? (
              <p>
                <bdi>{detail.description}</bdi>
              </p>
            ) : null}
            <p className="spaces-meta">{t("assignedTo", { names: detail.task.assignees.map((a) => a.displayName).join(", ") || t("nobody") })}</p>
          </>
        )}

        <section className="spaces-section" aria-labelledby="task-comments">
          <h2 id="task-comments">{t("commentsTitle")}</h2>
          {detail.comments.length === 0 ? <p className="spaces-meta">{t("noComments")}</p> : null}
          <ul className="spaces-list" aria-label={t("commentsTitle")}>
            {detail.comments.map((c) => (
              <li key={c.id}>
                <p className="spaces-meta">
                  {t("commentBy", { name: c.author.displayName, date: format.dateTime(new Date(c.createdAt), { dateStyle: "medium", timeStyle: "short" }) })}
                </p>
                <p>
                  <bdi>{c.body}</bdi>
                </p>
              </li>
            ))}
          </ul>
          {edit ? (
            <form
              className="spaces-form"
              onSubmit={(e) => {
                e.preventDefault();
                void run(() => send(`${url}/comments`, "POST", { body: comment }), () => setComment(""));
              }}
            >
              <TextAreaField label={t("comment")} name="comment" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={4000} required />
              <div className="spaces-row">
                <Button type="submit">{t("addComment")}</Button>
              </div>
            </form>
          ) : null}
        </section>
      </div>

      <aside className="spaces-section" aria-labelledby="task-checklist">
        <h2 id="task-checklist">{t("checklistTitle")}</h2>
        {checklist.length > 0 ? (
          <Progress label={t("checklistTitle")} value={Math.round((detail.task.checklist.done * 100) / Math.max(1, detail.task.checklist.total))}
            valueText={t("checked", { done: detail.task.checklist.done, total: detail.task.checklist.total })} />
        ) : (
          <p className="spaces-meta">{t("noChecklist")}</p>
        )}
        <ul className="spaces-list" aria-label={t("checklistTitle")}>
          {checklist.map((c) => (
            <li key={c.id} className="spaces-row">
              <Checkbox label={c.text} checked={c.done} disabled={!edit}
                onChange={(e) => void run(() => send(`${url}/checklist/${c.id}`, "PUT", { done: e.target.checked }))} />
              {edit ? (
                <IconButton icon="trash" label={t("removeItem", { text: c.text })} size="xs" onClick={() => void run(() => send(`${url}/checklist/${c.id}`, "DELETE"))} />
              ) : null}
            </li>
          ))}
        </ul>
        {edit ? (
          <form
            className="spaces-toolbar"
            onSubmit={(e) => {
              e.preventDefault();
              void run(() => send(`${url}/checklist`, "POST", { text: item }), () => setItem(""));
            }}
          >
            <FormField label={t("newItem")} name="item" value={item} onChange={(e) => setItem(e.target.value)} maxLength={300} required />
            <Button type="submit" icon="plus" size="sm">
              {t("addItem")}
            </Button>
          </form>
        ) : null}
        <section className="spaces-section" aria-labelledby="task-files">
          <h2 id="task-files">{t("filesTitle")}</h2>
          {detail.files.length === 0 ? <p className="spaces-meta">{t("noFiles")}</p> : null}
          <ul className="spaces-list" aria-label={t("filesTitle")}>
            {detail.files.map((f) => (
              <li key={f.documentId} className="spaces-row">
                <a href={documentHref(f.documentId)}>
                  <bdi>{f.name}</bdi>
                </a>
                <span className="spaces-meta">{t("fileVersion", { version: f.version })}</span>
                {edit ? (
                  <IconButton icon="close" label={t("unlinkFile", { name: f.name })} size="xs" onClick={() => void run(() => send(`${url}/files/${f.documentId}`, "DELETE"))} />
                ) : null}
              </li>
            ))}
          </ul>
          {edit && libraryFiles.length > 0 ? (
            <form
              className="spaces-toolbar"
              onSubmit={(e) => {
                e.preventDefault();
                void run(() => send(`${url}/files`, "POST", { documentId: file }), () => setFile(""));
              }}
            >
              <SelectField label={t("linkFileLabel")} name="file" value={file} onChange={(e) => setFile(e.target.value)} required
                options={[
                  { value: "", label: t("chooseFile") },
                  ...libraryFiles.filter((l) => !detail.files.some((f) => f.documentId === l.id)).map((l) => ({ value: l.id, label: l.name })),
                ]} />
              <Button type="submit" icon="link" size="sm">
                {t("linkFile")}
              </Button>
            </form>
          ) : null}
        </section>
        <p className="spaces-meta">
          {t("createdBy", { name: detail.createdBy.displayName, date: format.dateTime(new Date(detail.createdAt), { dateStyle: "medium" }) })}
        </p>
      </aside>
    </div>
  );
}
