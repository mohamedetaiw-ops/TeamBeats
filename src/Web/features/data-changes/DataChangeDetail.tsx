"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";
import { problemMessages } from "@/lib/notifications/types";
import { dataChangeTone, type DataChange } from "@/lib/data-changes";
import "./data-changes.css";

/**
 * One data change (spec 022 US6). The person withdraws it while it waits. HR, from the inbox task, checks the values
 * against the document and decides; without an HR connection HR enters the change in the HR system and marks it done.
 * The values disappear once the change is written, refused or withdrawn. The API checks every action again.
 */
export function DataChangeDetail({ initial, taskId }: { initial: DataChange; taskId: string | null }) {
  const t = useTranslations("data_changes");
  const format = useFormatter();
  const [change, setChange] = useState(initial);
  const [busy, setBusy] = useState<"withdraw" | "complete" | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function act(kind: "withdraw" | "complete", done: string) {
    setBusy(kind);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/hr/data-changes/${change.id}:${kind}`, { method: "POST" });
      if (response.ok) {
        setChange((await response.json()) as DataChange);
        setNotice(done);
      } else {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  async function reload() {
    const response = await fetch(`/api/v1/hr/data-changes/${change.id}`, { cache: "no-store" });
    if (response.ok) setChange((await response.json()) as DataChange);
    setNotice(t("detail.decided"));
  }

  const cleared = change.fields.every((f) => f.value === null);
  return (
    <div className="data-changes">
      <section className="data-changes-section" aria-labelledby="data-change-summary">
        <h2 id="data-change-summary">{t(`groups.${change.group}`)}</h2>
        {notice ? (
          <p className="notice" role="status">
            {notice}
          </p>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <dl className="data-changes-facts">
          <div>
            <dt>{t("detail.person")}</dt>
            <dd>
              <bdi>{change.person.displayName}</bdi>
            </dd>
          </div>
          <div>
            <dt>{t("detail.status")}</dt>
            <dd>
              <Badge tone={dataChangeTone[change.status]}>{t(`statuses.${change.status}`)}</Badge>
            </dd>
          </div>
          <div>
            <dt>{t("detail.asked")}</dt>
            <dd>{format.dateTime(new Date(change.createdAt), { dateStyle: "medium" })}</dd>
          </div>
          {change.externalReference ? (
            <div>
              <dt>{t("detail.hrReference")}</dt>
              <dd>
                <bdi dir="ltr">{change.externalReference}</bdi>
              </dd>
            </div>
          ) : null}
        </dl>
        {change.status === "approved" ? <p className="field-hint">{t(change.writeBack === "manual" ? "detail.manual" : "detail.writing")}</p> : null}
        {change.status === "write_failed" ? <p className="field-hint">{t("detail.writeFailed")}</p> : null}
        <h3>{t("detail.values")}</h3>
        {cleared ? <p className="field-hint">{t("detail.cleared")}</p> : null}
        <dl className="data-changes-facts">
          {change.fields.map((f) => (
            <div key={f.key}>
              <dt>{t(`fields.${f.key}`)}</dt>
              <dd>{f.value === null ? t("detail.notKept") : <bdi>{f.value}</bdi>}</dd>
            </div>
          ))}
        </dl>
        {change.reason ? (
          <>
            <h3>{t("reason")}</h3>
            <p className="data-changes-reason">{change.reason}</p>
          </>
        ) : null}
        <div className="data-changes-row">
          {change.rights.document && change.documentName ? (
            <a className="data-changes-link" href={`/api/v1/hr/data-changes/${change.id}/document`} download>
              {t("detail.document", { name: change.documentName })}
            </a>
          ) : null}
          {change.rights.withdraw ? (
            <Button busy={busy === "withdraw"} busyLabel={t("sending")} onClick={() => void act("withdraw", t("detail.withdrawn"))}>
              {t("detail.withdraw")}
            </Button>
          ) : null}
          {change.rights.complete ? (
            <Button variant="primary" busy={busy === "complete"} busyLabel={t("sending")} onClick={() => void act("complete", t("detail.completed"))}>
              {t("detail.complete")}
            </Button>
          ) : null}
        </div>
      </section>
      {taskId && change.rights.decide ? <TaskDecisionPanel taskId={taskId} title={t("detail.decide")} onDone={() => void reload()} /> : null}
    </div>
  );
}
