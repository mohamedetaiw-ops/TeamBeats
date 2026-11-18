"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import { PersonPicker } from "@/features/services/PersonPicker";
import { complianceApi, controlHref, evidenceFileHref, type AuditExportSummary, type ControlDetail, type TaskView } from "@/lib/compliance";

/**
 * Evidence tasks of a control (US7 scenario 2). Managers and the control's owner add tasks; a task's owner marks it
 * done or attaches an audit export, which is copied and keeps its export ID and SHA-256 (FR-023). Everyone who sees
 * the project, auditor guests included, downloads the evidence and can check the hash.
 */
export function EvidenceTasks({ detail }: { detail: ControlDetail }) {
  const t = useTranslations("compliance.tasks");
  const format = useFormatter();
  const router = useRouter();
  const action = useAdminAction(controlHref(detail.control.id));
  const [title, setTitle] = useState("");
  const [owner, setOwner] = useState<string | null>(null);
  const [due, setDue] = useState("");
  const [exports, setExports] = useState<AuditExportSummary[] | null>(null);
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const attachable = detail.tasks.some((task) => task.canAttach);

  useEffect(() => {
    if (!attachable) return;
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`${complianceApi}/exports`, { cache: "no-store" });
        if (live) setExports(response.ok ? ((await response.json()) as AuditExportSummary[]) : null);
      } catch {
        // Without the list, exports cannot be attached from here.
      }
    })();
    return () => {
      live = false;
    };
  }, [attachable]);

  const day = (value: string) => format.dateTime(new Date(`${value}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium", timeStyle: "short" });

  async function add(e: FormEvent) {
    e.preventDefault();
    const outcome = await action.run("add", "POST", `${complianceApi}/controls/${detail.control.id}/tasks`, { title, ownerId: owner, dueOn: due || null }, t("added"));
    if (outcome.ok) {
      setTitle("");
      setOwner(null);
      setDue("");
      router.refresh();
    }
  }

  async function done(task: TaskView) {
    const outcome = await action.run("done:" + task.id, "PUT", `${complianceApi}/tasks/${task.id}`, { done: !task.doneAt }, t(task.doneAt ? "reopened" : "completed"));
    if (outcome.ok) router.refresh();
  }

  async function remove(task: TaskView) {
    const outcome = await action.run("delete:" + task.id, "DELETE", `${complianceApi}/tasks/${task.id}`, undefined, t("deleted"));
    if (outcome.ok) router.refresh();
  }

  async function attach(task: TaskView) {
    const exportId = chosen[task.id] ?? exports?.[0]?.id;
    const outcome = await action.run("attach:" + task.id, "POST", `${complianceApi}/tasks/${task.id}/evidence`, { exportId }, t("attached"));
    if (outcome.ok) router.refresh();
  }

  return (
    <section className="compliance-section" aria-labelledby="control-tasks">
      <h2 id="control-tasks">{t("title")}</h2>
      <ResultAlert result={action.result} />
      {detail.tasks.length === 0 ? <p className="compliance-meta">{t("none")}</p> : null}
      <ul className="compliance-list">
        {detail.tasks.map((task) => (
          <li key={task.id} className="compliance-task">
            <div className="compliance-row">
              <strong>
                <bdi>{task.title}</bdi>
              </strong>
              <Badge tone={task.doneAt ? "success" : task.overdue ? "warning" : "neutral"}>{t(task.doneAt ? "done" : task.overdue ? "overdue" : "open")}</Badge>
            </div>
            <p className="compliance-meta">
              {task.dueOn
                ? t("ownerDue", { name: task.owner?.displayName ?? t("noOwner"), date: day(task.dueOn) })
                : t("owner", { name: task.owner?.displayName ?? t("noOwner") })}
            </p>
            {task.evidence.length > 0 ? (
              <ul className="compliance-list" aria-label={t("evidence")}>
                {task.evidence.map((e) => (
                  <li key={e.id}>
                    <a href={evidenceFileHref(e.id)} download>
                      {t("exportFile", { format: e.format.toUpperCase(), rows: e.rowCount ?? 0 })}
                    </a>
                    <p className="compliance-meta">{t("attachedBy", { name: e.attachedBy?.displayName ?? "", date: when(e.attachedAt) })}</p>
                    <p className="compliance-meta">
                      {t("exportId")} <bdi dir="ltr" className="compliance-hash">{e.sourceId}</bdi>
                    </p>
                    <p className="compliance-meta">
                      {t("sha256")} <bdi dir="ltr" className="compliance-hash">{e.sha256}</bdi>
                    </p>
                  </li>
                ))}
              </ul>
            ) : null}
            {task.canAttach || task.canEdit ? (
              <div className="ui-control-row">
                {task.canAttach ? (
                  <Button size="sm" icon={task.doneAt ? "minus" : "check"} busy={action.busy === "done:" + task.id} busyLabel={t("working")} onClick={() => void done(task)}>
                    {t(task.doneAt ? "reopen" : "complete")}
                  </Button>
                ) : null}
                {task.canAttach && exports && exports.length > 0 ? (
                  <>
                    <SelectField
                      label={t("export")}
                      name={`export-${task.id}`}
                      value={chosen[task.id] ?? exports[0]!.id}
                      onChange={(e) => setChosen((c) => ({ ...c, [task.id]: e.target.value }))}
                      options={exports.map((x) => ({ value: x.id, label: t("exportOption", { format: x.format.toUpperCase(), date: when(x.completedAt) }) }))}
                    />
                    <Button size="sm" icon="attach" busy={action.busy === "attach:" + task.id} busyLabel={t("working")} onClick={() => void attach(task)}>
                      {t("attach")}
                    </Button>
                  </>
                ) : null}
                {task.canEdit && task.evidence.length === 0 ? (
                  <ConfirmAction label={t("delete")} question={t("deleteQuestion", { title: task.title })} confirmLabel={t("delete")} cancelLabel={t("cancel")} onConfirm={() => remove(task)} />
                ) : null}
              </div>
            ) : null}
          </li>
        ))}
      </ul>
      {exportHint(attachable, exports, t)}
      {detail.rights.canAddTasks ? (
        <form onSubmit={(e) => void add(e)} aria-labelledby="control-task-add">
          <h3 id="control-task-add">{t("addTitle")}</h3>
          <div className="admin-grid">
            <FormField label={t("taskTitle")} hint={t("taskTitleHint")} name="title" required maxLength={200} value={title} onChange={(e) => setTitle(e.target.value)} />
            <FormField label={t("dueOn")} name="dueOn" type="date" value={due} onChange={(e) => setDue(e.target.value)} />
          </div>
          <PersonPicker label={t("taskOwner")} value={owner} onChange={setOwner} />
          <div className="ui-control-row">
            <Button type="submit" variant="primary" icon="plus" busy={action.busy === "add"} busyLabel={t("working")}>
              {t("add")}
            </Button>
          </div>
        </form>
      ) : null}
    </section>
  );
}

/** Why the export choice is missing: attaching needs the right to export the audit trail, and a completed export. */
function exportHint(attachable: boolean, exports: AuditExportSummary[] | null, t: (key: string) => string) {
  if (!attachable) return null;
  if (exports === null) return <p className="compliance-meta">{t("exportsDenied")}</p>;
  if (exports.length === 0) return <p className="compliance-meta">{t("exportsNone")}</p>;
  return null;
}
