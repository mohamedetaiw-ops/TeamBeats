"use client";

import { requestTone } from "@/lib/services/status";
import Link from "next/link";
import { Fragment, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { problemMessages } from "@/lib/notifications/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { pick, type FieldValue, type FormField, type RequestDetail } from "@/lib/services/types";
import { AutomationPanel } from "./AutomationPanel";
import { TaskDecisionPanel } from "./TaskDecisionPanel";
import { isolate } from "@/lib/bidi";
import "./services.css";

const cancellable = new Set(["submitted", "in_approval", "returned"]);
const decisions = new Set(["approved", "rejected", "returned"]);
// Tracking events that have a message; any other shows as the API sent it.
const tracked = new Set(["submitted", "resubmitted", "cancelled", "approved", "rejected", "returned", "completed",
  "automation_done", "automation_failed", "automation_retried", "automation_skipped", "automation_cancelled"]);

/**
 * Details and Tracking of one request (spec 016 FR-005): status, current step and assignees, the submitted values on
 * the form version they were filled with, attachments, and every step in order. The filer may cancel it while it is
 * open, and change and submit it again after a return. An approver who opens it from the inbox decides here.
 */
export function RequestDetailView({ detail, meId, units, taskId }: { detail: RequestDetail; meId: string; units: OrgUnit[]; taskId: string | null }) {
  const t = useTranslations("services.request");
  const ts = useTranslations("services.status");
  const locale = useLocale();
  const format = useFormatter();
  const router = useRouter();
  const [problem, setProblem] = useState<string[]>([]);
  const r = detail.request;
  const when = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });

  function show(f: FormField, value: FieldValue | undefined): string {
    if (value === undefined || value === null || value === "") return t("empty");
    switch (f.type) {
      case "choice":
      case "lookup":
        return pick(f.options?.find((o) => o.value === value)?.labels, locale, String(value));
      case "multi_choice":
        return (value as string[]).map((v) => pick(f.options?.find((o) => o.value === v)?.labels, locale, v)).join(t("separator"));
      case "money": {
        const m = value as { amount: number; currency: string };
        return format.number(m.amount, { style: "currency", currency: m.currency });
      }
      case "date_range": {
        const d = value as { from: string; to: string };
        return t("range", { from: d.from, to: d.to });
      }
      case "org_unit": {
        const unit = units.find((u) => u.id === value);
        return unit ? unitName(unit, locale) : String(value);
      }
      case "attachment":
        return t("files", { count: Array.isArray(value) ? value.length : 1 });
      case "person":
        return t("person");
      default:
        return String(value);
    }
  }

  async function cancel() {
    const response = await fetch(`/api/v1/requests/${r.id}/cancel`, { method: "POST" });
    if (response.ok) {
      router.refresh();
    } else {
      setProblem(await problemMessages(response));
    }
  }

  const isFiler = r.requester.id === meId;
  return (
    <div className="services">
      <section className="services-section" aria-labelledby="request-summary">
        <h2 id="request-summary">{t("summary")}</h2>
        <dl className="services-details">
          <dt>{t("number")}</dt>
          <dd>
            <bdi dir="ltr">{r.number}</bdi>
          </dd>
          <dt>{t("statusLabel")}</dt>
          <dd>
            <span className="ui-badge" data-tone={requestTone(r.status)} data-testid="request-status">
              {ts(r.status)}
            </span>
          </dd>
          {r.currentStep ? (
            <>
              <dt>{t("step")}</dt>
              <dd>
                <bdi>{pick(r.currentStep, locale)}</bdi>
              </dd>
            </>
          ) : null}
          {r.assignees.length > 0 ? (
            <>
              <dt>{t("assignees")}</dt>
              <dd>
                <bdi>{r.assignees.map((a) => a.displayName).join(t("separator"))}</bdi>
              </dd>
            </>
          ) : null}
          <dt>{t("requester")}</dt>
          <dd>
            <bdi>{r.requester.displayName}</bdi>
          </dd>
          {r.onBehalfOf ? (
            <>
              <dt>{t("for")}</dt>
              <dd>
                <bdi>{r.onBehalfOf.displayName}</bdi>
              </dd>
            </>
          ) : null}
          <dt>{t("submitted")}</dt>
          <dd>{r.submittedAt ? when(r.submittedAt) : t("empty")}</dd>
        </dl>
        <div className="services-actions">
          {isFiler && r.status === "returned" ? <Link href={`/requests/${r.id}/edit`}>{t("change")}</Link> : null}
          {isFiler && cancellable.has(r.status) ? (
            <ConfirmAction label={t("cancel")} question={t("cancelQuestion")} confirmLabel={t("cancelConfirm")} cancelLabel={t("keep")} onConfirm={cancel} />
          ) : null}
        </div>
        {problem.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {problem.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </section>

      {taskId && r.status === "in_approval" ? <TaskDecisionPanel taskId={taskId} onDone={() => router.push("/tasks")} /> : null}

      <section className="services-section" aria-labelledby="request-values">
        <h2 id="request-values">{t("values")}</h2>
        <dl className="services-details">
          {detail.form.fields
            .filter((f) => f.type !== "policy_note" && detail.data[f.key] !== undefined)
            .map((f) => (
              <Fragment key={f.key}>
                <dt>{pick(f.labels, locale, f.key)}</dt>
                <dd>
                  <bdi>{show(f, detail.data[f.key])}</bdi>
                </dd>
              </Fragment>
            ))}
        </dl>
        {detail.attachments.length > 0 ? (
          <>
            <h3>{t("attachments")}</h3>
            <ul className="services-list">
              {detail.attachments.map((a) => (
                <li key={a.id} className="services-row">
                  {a.scanState === "clean" ? (
                    <a href={`/api/v1/requests/${r.id}/attachments/${a.id}`}>
                      <bdi>{a.fileName}</bdi>
                    </a>
                  ) : (
                    <bdi>{a.fileName}</bdi>
                  )}
                  <span className="services-status">{t(`scan.${a.scanState}`)}</span>
                </li>
              ))}
            </ul>
          </>
        ) : null}
      </section>

      {detail.automation && detail.automation.steps.length > 0 ? <AutomationPanel requestId={r.id} automation={detail.automation} /> : null}

      <section className="services-section" aria-labelledby="request-tracking">
        <h2 id="request-tracking">{t("tracking")}</h2>
        <ol className="services-list" data-testid="request-tracking">
          {detail.tracking.map((e, i) => (
            <li key={`${e.at}-${i}`} className="services-item">
              <strong>{tracked.has(e.event) ? t(`event.${e.event}`) : e.event}</strong>
              <span className="services-meta">
                {when(e.at)}
                {e.actor ? (
                  <>
                    {" · "}
                    <bdi>{e.actor.displayName}</bdi>
                  </>
                ) : null}
                {e.onBehalfOf ? (
                  <>
                    {" · "}
                    {decisions.has(e.event) ? t("onBehalfOf", { name: isolate(e.onBehalfOf.displayName) }) : t("filedFor", { name: isolate(e.onBehalfOf.displayName) })}
                  </>
                ) : null}
              </span>
              {e.step ? <bdi>{e.step}</bdi> : null}
              {e.comment ? <p className="services-note">{e.comment}</p> : null}
            </li>
          ))}
        </ol>
      </section>
    </div>
  );
}
