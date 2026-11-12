"use client";

import { useState } from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { Progress } from "@/components/ui/Status";
import { StateView } from "@/components/ui/StateView";
import { call, send, spacesApi, statusValues, taskHref, type MilestoneView, type SpaceOverview, type SpaceView } from "@/lib/spaces";
import { DueBadge, StatusBadge } from "./parts";
import "./spaces.css";

/**
 * The space overview (spec 029 US3): the owner's status with its note and history, progress per milestone and the
 * overdue tasks. Owners set the status and members add milestones; the API decides who may, the page only offers it.
 */
export function Overview({ space, initial }: { space: SpaceView; initial: SpaceOverview }) {
  const t = useTranslations("spaces.overview");
  const format = useFormatter();
  const [overview, setOverview] = useState(initial);
  const [status, setStatus] = useState<string>(initial.status?.status ?? "on_track");
  const [note, setNote] = useState("");
  const [milestone, setMilestone] = useState("");
  const [milestoneDue, setMilestoneDue] = useState("");
  const [messages, setMessages] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const url = `${spacesApi}/${space.id}`;

  async function setSpaceStatus() {
    setBusy(true);
    const result = await call<SpaceOverview>(() => send(`${url}/status`, "POST", { status, note }), t("failed"));
    setBusy(false);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setNote("");
    setOverview(result.value);
  }

  async function addMilestone() {
    const result = await call<MilestoneView[]>(() => send(`${url}/milestones`, "POST", { name: milestone, dueOn: milestoneDue || null }), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setMilestone("");
    setMilestoneDue("");
    setOverview((o) => ({ ...o, milestones: result.value }));
  }

  const when = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });

  return (
    <div className="spaces-split">
      <div className="spaces">
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {space.status === "archived" ? <Alert tone="warning" title={t("archivedTitle")}>{t("archived")}</Alert> : null}
        {space.description ? (
          <p>
            <bdi>{space.description}</bdi>
          </p>
        ) : null}

        <section className="spaces-section" aria-labelledby="space-status">
          <h2 id="space-status">{t("statusTitle")}</h2>
          {overview.status ? (
            <>
              <div className="spaces-row">
                <StatusBadge status={overview.status.status} />
                <span className="spaces-meta">{t("setBy", { name: overview.status.setBy.displayName, date: when(overview.status.setAt) })}</span>
              </div>
              {overview.status.note ? (
                <p>
                  <bdi>{overview.status.note}</bdi>
                </p>
              ) : null}
            </>
          ) : (
            <p className="spaces-meta">{t("noStatus")}</p>
          )}
          {overview.canSetStatus ? (
            <form
              className="spaces-form"
              onSubmit={(e) => {
                e.preventDefault();
                void setSpaceStatus();
              }}
            >
              <SelectField label={t("status")} name="status" value={status} onChange={(e) => setStatus(e.target.value)}
                options={statusValues.map((v) => ({ value: v, label: t(`statusOption.${v}`) }))} />
              <TextAreaField label={t("note")} name="note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={2000} />
              <div className="spaces-row">
                <Button type="submit" busy={busy} busyLabel={t("saving")}>
                  {t("setStatus")}
                </Button>
              </div>
            </form>
          ) : null}
          {overview.history.length > 1 ? (
            <details>
              <summary>{t("history")}</summary>
              <ul className="spaces-list" aria-label={t("history")}>
                {overview.history.map((h) => (
                  <li key={h.id}>
                    <div className="spaces-row">
                      <StatusBadge status={h.status} />
                      <span className="spaces-meta">{t("setBy", { name: h.setBy.displayName, date: when(h.setAt) })}</span>
                    </div>
                    {h.note ? <p className="spaces-meta"><bdi>{h.note}</bdi></p> : null}
                  </li>
                ))}
              </ul>
            </details>
          ) : null}
        </section>

        <section className="spaces-section" aria-labelledby="space-milestones">
          <h2 id="space-milestones">{t("milestonesTitle")}</h2>
          {overview.milestones.length === 0 ? (
            <p className="spaces-meta">{t("noMilestones")}</p>
          ) : (
            <ul className="spaces-list" aria-label={t("milestonesTitle")}>
              {overview.milestones.map((m) => (
                <li key={m.id}>
                  <div className="spaces-row">
                    <strong>
                      <bdi>{m.name}</bdi>
                    </strong>
                    <DueBadge dueOn={m.dueOn} overdue={m.overdue} />
                    {m.reached ? <span className="spaces-meta">{t("reached")}</span> : null}
                  </div>
                  <Progress label={t("progressOf", { name: m.name })} value={m.total === 0 ? 0 : Math.round((m.done * 100) / m.total)}
                    valueText={t("progress", { done: m.done, total: m.total })} />
                </li>
              ))}
            </ul>
          )}
          {overview.canEdit ? (
            <form
              className="spaces-toolbar"
              onSubmit={(e) => {
                e.preventDefault();
                void addMilestone();
              }}
            >
              <FormField label={t("milestoneName")} name="milestone" value={milestone} onChange={(e) => setMilestone(e.target.value)} maxLength={80} required />
              <FormField label={t("milestoneDue")} name="milestoneDue" type="date" value={milestoneDue} onChange={(e) => setMilestoneDue(e.target.value)} />
              <Button type="submit" icon="plus">
                {t("addMilestone")}
              </Button>
            </form>
          ) : null}
        </section>
      </div>

      <aside className="spaces-section" aria-labelledby="space-overdue">
        <h2 id="space-overdue">{t("tasksTitle")}</h2>
        <p className="spaces-meta">{t("counts", { open: overview.openTasks, done: overview.doneTasks })}</p>
        {overview.overdue.length === 0 ? (
          <StateView kind="empty" title={t("noOverdueTitle")} message={t("noOverdue")} icon="check" />
        ) : (
          <>
            <h3>{t("overdueTitle", { count: overview.overdueTasks })}</h3>
            <ul className="spaces-list" aria-label={t("overdueList")}>
              {overview.overdue.map((task) => (
                <li key={task.id}>
                  <Link href={taskHref(space.id, task.id)}>
                    <bdi>{task.title}</bdi>
                  </Link>
                  <DueBadge dueOn={task.dueOn} overdue />
                </li>
              ))}
            </ul>
          </>
        )}
        <p className="spaces-meta">{t("owners", { names: space.owners.map((o) => o.displayName).join(", ") })}</p>
      </aside>
    </div>
  );
}
