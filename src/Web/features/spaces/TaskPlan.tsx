"use client";

import { useCallback, useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { IconButton } from "@/components/ui/IconButton";
import { Meter } from "@/components/ui/Status";
import { Widget } from "@/components/ui/Widget";
import {
  announceTaskChanged,
  call,
  hoursOf,
  send,
  spacesApi,
  taskChangedEvent,
  taskHref,
  type LinkedTask,
  type TaskSchedule,
  type TaskTime,
  type TimeEntryView,
} from "@/lib/spaces";
import "./spaces.css";

/**
 * A task's plan and time (spec 029 R3 US8, US9): its start, due date and estimate, the tasks it waits for and the tasks
 * waiting for it, and the time people logged on it. The dates form is the keyboard way to move a bar on the timeline.
 * The API decides who may change what; this part shows what it answers.
 */
export function TaskPlan({
  spaceId,
  initialSchedule,
  initialTime,
  tasks,
  today,
}: {
  spaceId: string;
  initialSchedule: TaskSchedule;
  initialTime: TaskTime;
  /** The other tasks of the space a blocker can be chosen from. */
  tasks: { id: string; title: string }[];
  today: string;
}) {
  const t = useTranslations("spaces.plan");
  const format = useFormatter();
  const taskId = initialSchedule.taskId;
  const url = `${spacesApi}/${spaceId}/tasks/${taskId}`;
  const [schedule, setSchedule] = useState(initialSchedule);
  const [time, setTime] = useState(initialTime);
  const [startOn, setStartOn] = useState(initialSchedule.startOn ?? "");
  const [dueOn, setDueOn] = useState(initialSchedule.dueOn ?? "");
  const [estimate, setEstimate] = useState(initialSchedule.estimateMinutes ? String(initialSchedule.estimateMinutes / 60) : "");
  const [blocker, setBlocker] = useState("");
  const [workedOn, setWorkedOn] = useState(today);
  const [hours, setHours] = useState("");
  const [minutes, setMinutes] = useState("");
  const [note, setNote] = useState("");
  const [editing, setEditing] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  const reload = useCallback(async () => {
    const result = await call<TaskSchedule>(() => fetch(`${url}/schedule`), t("failed"));
    if (!result.ok) return;
    setSchedule(result.value);
    setStartOn(result.value.startOn ?? "");
    setDueOn(result.value.dueOn ?? "");
  }, [url, t]);

  // The task form and the complete button change the version and the due date: read the plan again.
  useEffect(() => {
    const listen = (e: Event) => {
      if ((e as CustomEvent<{ taskId: string }>).detail.taskId === taskId) void reload();
    };
    window.addEventListener(taskChangedEvent, listen);
    return () => window.removeEventListener(taskChangedEvent, listen);
  }, [taskId, reload]);

  async function plan(request: () => Promise<Response>, announce: boolean) {
    setSaved(false);
    const result = await call<TaskSchedule>(request, t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return false;
    }
    setMessages([]);
    setSchedule(result.value);
    if (announce) announceTaskChanged(taskId);
    return true;
  }

  async function logged(request: () => Promise<Response>) {
    const result = await call<TaskTime>(request, t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return false;
    }
    setMessages([]);
    setTime(result.value);
    return true;
  }

  function duration(total: number) {
    return t("duration", hoursOf(total));
  }

  function linked(list: LinkedTask[], remove: boolean) {
    return (
      <ul className="spaces-list">
        {list.map((l) => (
          <li key={l.id} className="spaces-row">
            <a href={taskHref(spaceId, l.id)}>
              <bdi>{l.title}</bdi>
            </a>
            {l.status === "done" ? <Badge tone="success">{t("done")}</Badge> : null}
            {l.conflict ? <Badge tone="danger" icon="alert">{t("conflictBadge")}</Badge> : null}
            {remove && schedule.canEdit ? (
              <IconButton icon="close" size="xs" label={t("removeBlocker", { title: l.title })}
                onClick={() => void plan(() => send(`${url}/blockers/${l.id}`, "DELETE"), false)} />
            ) : null}
          </li>
        ))}
      </ul>
    );
  }

  function edit(entry: TimeEntryView) {
    setEditing(entry.id);
    setWorkedOn(entry.workedOn);
    setHours(String(Math.floor(entry.minutes / 60)));
    setMinutes(String(entry.minutes % 60));
    setNote(entry.note);
  }

  function clearEntry() {
    setEditing(null);
    setWorkedOn(today);
    setHours("");
    setMinutes("");
    setNote("");
  }

  const choices = tasks.filter((x) => x.id !== taskId && !schedule.blockedBy.some((b) => b.id === x.id));
  const spent = Number(hours || 0) * 60 + Number(minutes || 0);

  return (
    <div className="spaces-split">
      <Widget title={t("planTitle")}>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("saved")} live /> : null}
        {schedule.conflict ? (
          <Alert tone="warning" title={t("conflictTitle")}>
            {t("conflict")}
          </Alert>
        ) : null}
        {schedule.finishedBeforeBlocker ? <p className="spaces-meta">{t("finishedBefore")}</p> : null}
        {schedule.canEdit ? (
          <form
            className="spaces-form"
            onSubmit={(e) => {
              e.preventDefault();
              const body = { startOn: startOn || null, dueOn: dueOn || null, estimateMinutes: estimate ? Math.round(Number(estimate) * 60) : null, version: schedule.version };
              void plan(() => send(`${url}/schedule`, "PUT", body), true).then(setSaved);
            }}
          >
            <div className="spaces-toolbar">
              <FormField label={t("startOn")} name="startOn" type="date" value={startOn} onChange={(e) => setStartOn(e.target.value)} />
              <FormField label={t("dueOn")} name="planDueOn" type="date" value={dueOn} onChange={(e) => setDueOn(e.target.value)} />
              <FormField label={t("estimate")} hint={t("estimateHint")} name="estimate" type="number" min={0} step={0.25} value={estimate}
                onChange={(e) => setEstimate(e.target.value)} />
            </div>
            <div className="spaces-row">
              <Button type="submit">{t("saveDates")}</Button>
            </div>
          </form>
        ) : (
          <p className="spaces-meta">
            {t("datesRead", {
              start: schedule.startOn ? format.dateTime(new Date(`${schedule.startOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) : t("none"),
              due: schedule.dueOn ? format.dateTime(new Date(`${schedule.dueOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) : t("none"),
            })}
          </p>
        )}

        <h3>{t("blockedByTitle")}</h3>
        {schedule.blockedBy.length === 0 ? <p className="spaces-meta">{t("noBlockers")}</p> : linked(schedule.blockedBy, true)}
        {schedule.canEdit && choices.length > 0 ? (
          <form
            className="spaces-toolbar"
            onSubmit={(e) => {
              e.preventDefault();
              void plan(() => send(`${url}/blockers`, "POST", { taskId: blocker }), false).then((ok) => ok && setBlocker(""));
            }}
          >
            <SelectField label={t("addBlockerLabel")} name="blocker" value={blocker} onChange={(e) => setBlocker(e.target.value)} required
              options={[{ value: "", label: t("chooseTask") }, ...choices.map((c) => ({ value: c.id, label: c.title }))]} />
            <Button type="submit" icon="link" size="sm">
              {t("addBlocker")}
            </Button>
          </form>
        ) : null}
        <h3>{t("blocksTitle")}</h3>
        {schedule.blocks.length === 0 ? <p className="spaces-meta">{t("noBlocks")}</p> : linked(schedule.blocks, false)}
      </Widget>

      <Widget title={t("timeTitle")}>
        {time.estimateMinutes ? (
          <Meter label={t("timeTitle")} value={time.loggedMinutes} max={Math.max(time.estimateMinutes, time.loggedMinutes)}
            valueText={t("loggedOf", { logged: duration(time.loggedMinutes), estimate: duration(time.estimateMinutes) })} />
        ) : (
          <p className="spaces-meta">{t("logged", { logged: duration(time.loggedMinutes) })}</p>
        )}
        {time.entries.length === 0 ? <p className="spaces-meta">{t("noTime")}</p> : null}
        <ul className="spaces-list" aria-label={t("entriesLabel")}>
          {time.entries.map((entry) => (
            <li key={entry.id} className="spaces-row">
              <span>
                {t("entry", {
                  name: entry.person.displayName,
                  date: format.dateTime(new Date(`${entry.workedOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }),
                  duration: duration(entry.minutes),
                })}
              </span>
              {entry.note ? (
                <span className="spaces-meta">
                  <bdi>{entry.note}</bdi>
                </span>
              ) : null}
              {entry.canEdit ? <IconButton icon="edit" size="xs" label={t("editEntry")} onClick={() => edit(entry)} /> : null}
              {entry.canRemove ? (
                <IconButton icon="trash" size="xs" label={t("removeEntry")} onClick={() => void logged(() => send(`${url}/time/${entry.id}`, "DELETE"))} />
              ) : null}
            </li>
          ))}
        </ul>
        {time.canLog ? (
          <form
            className="spaces-form"
            onSubmit={(e) => {
              e.preventDefault();
              const body = { workedOn, minutes: spent, note };
              void logged(() => (editing ? send(`${url}/time/${editing}`, "PUT", body) : send(`${url}/time`, "POST", body))).then((ok) => ok && clearEntry());
            }}
          >
            <div className="spaces-toolbar">
              <FormField label={t("workedOn")} name="workedOn" type="date" value={workedOn} max={today} onChange={(e) => setWorkedOn(e.target.value)} required />
              <FormField label={t("hours")} name="hours" type="number" min={0} max={24} value={hours} onChange={(e) => setHours(e.target.value)} />
              <FormField label={t("minutes")} name="minutes" type="number" min={0} max={59} value={minutes} onChange={(e) => setMinutes(e.target.value)} />
            </div>
            <TextAreaField label={t("note")} name="note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} />
            <div className="spaces-row">
              <Button type="submit" variant="primary">
                {editing ? t("saveEntry") : t("logTime")}
              </Button>
              {editing ? (
                <Button variant="quiet" onClick={clearEntry}>
                  {t("cancel")}
                </Button>
              ) : null}
            </div>
          </form>
        ) : null}
      </Widget>
    </div>
  );
}
