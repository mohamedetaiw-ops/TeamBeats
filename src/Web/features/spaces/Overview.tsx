"use client";

import { useState } from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { Progress } from "@/components/ui/Status";
import { StateView } from "@/components/ui/StateView";
import { call, send, spacesApi, statusValues, taskHref, type MilestoneView, type SpaceOverview, type SpaceView, type StatusSummary } from "@/lib/spaces";
import { DueBadge, StatusBadge } from "./parts";
import "./spaces.css";
import { Widget } from "@/components/ui/Widget";

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
  const [summary, setSummary] = useState<StatusSummary | null>(null);
  const [summarizing, setSummarizing] = useState(false);
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

  // R3 US11: a summary written through the AI Gateway from this space only. It is shown, marked, and kept only if an owner saves it as a note.
  async function summarize() {
    setSummarizing(true);
    const result = await call<StatusSummary>(() => send(`${url}/status-summary`, "POST"), t("failed"));
    setSummarizing(false);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setSummary(result.value);
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

        <Widget title={t("statusTitle")}>
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
          <div className="spaces-row">
            <Button icon="spark" variant="secondary" size="sm" busy={summarizing} busyLabel={t("summarizing")} onClick={() => void summarize()}>
              {t("summarize")}
            </Button>
          </div>
          {summary?.outcome === "summarized" && summary.summary ? (
            <section className="spaces-form" aria-label={t("summaryTitle")} data-testid="space-status-summary">
              <div className="spaces-row">
                <strong>{t("summaryTitle")}</strong>
                <Badge tone="info" icon="spark">{t("aiGenerated")}</Badge>
              </div>
              <p>
                <bdi>{summary.summary}</bdi>
              </p>
              <p className="spaces-meta">{t("summaryHint", { count: summary.tasks })}</p>
              {overview.canSetStatus ? (
                <div className="spaces-row">
                  <Button size="sm" onClick={() => setNote(summary.summary ?? "")}>
                    {t("useSummary")}
                  </Button>
                </div>
              ) : null}
            </section>
          ) : null}
          {summary && summary.outcome !== "summarized" ? (
            <Alert tone="warning" title={t(summary.outcome === "policy_blocked" ? "summaryBlockedTitle" : "summaryUnavailableTitle")} live>
              {t(summary.outcome === "policy_blocked" ? "summaryBlocked" : "summaryUnavailable")}
            </Alert>
          ) : null}
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
        </Widget>

        <Widget title={t("milestonesTitle")}>
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
        </Widget>
      </div>

      <Widget title={t("tasksTitle")}>
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
      </Widget>
    </div>
  );
}
