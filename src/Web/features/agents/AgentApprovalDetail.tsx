"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";
import { pickText, type AgentApprovalView } from "@/lib/agents/types";

function show(value: unknown): string {
  return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * An agent action waiting for its approver (spec 027 US4 scenario 1): the agent, the person it acts for, its reasoning
 * summary and the exact parameters, which the approval is bound to. The decision goes through the task inbox, which
 * checks the assignment and separation of duties again; the action then runs as the person, never as the approver.
 */
export function AgentApprovalDetail({ approval, taskId }: { approval: AgentApprovalView; taskId: string | null }) {
  const t = useTranslations("agents");
  const locale = useLocale();
  const format = useFormatter();
  const [decided, setDecided] = useState<string | null>(null);
  const tool = t.has(`toolNames.${approval.tool}`) ? t(`toolNames.${approval.tool}`) : approval.tool;
  const open = approval.status === "pending" && taskId !== null && decided === null;
  return (
    <div className="agents" data-testid="agent-approval">
      <section className="agents-section" aria-labelledby="agent-approval-summary">
        <h2 id="agent-approval-summary">{t("approval.summaryTitle")}</h2>
        <dl className="agents-card">
          <div className="agents-card-row">
            <dt>{t("approval.agent")}</dt>
            <dd>
              <bdi>{pickText(approval.agentNames, locale)}</bdi> <span className="agents-badge">{t("approval.byAgent")}</span>
            </dd>
          </div>
          <div className="agents-card-row">
            <dt>{t("approval.requester")}</dt>
            <dd>
              <bdi>{approval.requesterName ?? t("approval.unknownPerson")}</bdi>
            </dd>
          </div>
          <div className="agents-card-row">
            <dt>{t("approval.tool")}</dt>
            <dd>
              <bdi>{tool}</bdi>
            </dd>
          </div>
          <div className="agents-card-row">
            <dt>{t("approval.reasoning")}</dt>
            <dd>
              <bdi>{approval.summary ?? t("approval.noReasoning")}</bdi>
            </dd>
          </div>
          <div className="agents-card-row">
            <dt>{t("approval.due")}</dt>
            <dd>{format.dateTime(new Date(approval.dueAt), { dateStyle: "medium", timeStyle: "short" })}</dd>
          </div>
        </dl>
      </section>
      <section className="agents-section" aria-labelledby="agent-approval-parameters">
        <h2 id="agent-approval-parameters">{t("approval.parametersTitle")}</h2>
        <p>{t("approval.parametersHint")}</p>
        <dl className="agents-card">
          {Object.entries(approval.parameters).map(([key, value]) => (
            <div key={key} className="agents-card-row">
              <dt>
                <bdi>{key}</bdi>
              </dt>
              <dd>
                <bdi>{show(value)}</bdi>
              </dd>
            </div>
          ))}
        </dl>
      </section>
      {open ? (
        <TaskDecisionPanel taskId={taskId!} title={t("approval.decisionTitle")} onDone={(outcome) => setDecided(outcome)} />
      ) : (
        <p role="status" className="notice">
          {decided ? t("approval.decided") : t(`approval.status.${approval.status}`)}
        </p>
      )}
    </div>
  );
}
