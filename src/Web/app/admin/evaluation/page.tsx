import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { AssistantRuns, EvalAgentRow } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { StartAssistantRun } from "@/features/evaluation/StartRun";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

/**
 * The evaluation overview (spec 034 US6): the agents whose evaluation the viewer may read, with the gate of their latest
 * version, and for those who may read them the runs of the assistant's platform suites (spec 014 T008).
 */
export default async function EvaluationOverviewPage() {
  await evaluationSection();
  const [agents, assistant] = await Promise.all([
    apiGet<EvalAgentRow[]>("/api/v1/evaluation/agents"),
    apiGet<AssistantRuns>("/api/v1/evaluation/assistant/runs"),
  ]);
  if (!agents.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  return (
    <>
      <h1>{t("title")}</h1>
      <EvaluationTabs current="agents" />
      <div className="agents">
        <section className="agents-section" aria-labelledby="eval-agents">
          <h2 id="eval-agents">{t("overview.title")}</h2>
          {agents.data.length === 0 ? (
            <p>{t("overview.empty")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="eval-agents">
                <thead>
                  <tr>
                    <th scope="col">{t("overview.agent")}</th>
                    <th scope="col">{t("overview.status")}</th>
                    <th scope="col">{t("overview.current")}</th>
                    <th scope="col">{t("overview.latest")}</th>
                    <th scope="col">{t("overview.gate")}</th>
                  </tr>
                </thead>
                <tbody>
                  {agents.data.map((a) => (
                    <tr key={a.agentId}>
                      <th scope="row">
                        <Link href={`/admin/evaluation/agents/${a.agentId}`}>
                          <bdi>{pickText(a.name, locale) || t("untitled")}</bdi>
                        </Link>
                      </th>
                      <td>{t.has(`agentStatus.${a.status}`) ? t(`agentStatus.${a.status}`) : a.status}</td>
                      <td data-number>{a.currentVersion ?? t("none")}</td>
                      <td data-number>{a.latestVersion}</td>
                      <td>{t.has(`gate.${a.gateStatus}`) ? t(`gate.${a.gateStatus}`) : a.gateStatus}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
        {assistant.ok ? (
          <section className="agents-section" aria-labelledby="eval-assistant">
            <h2 id="eval-assistant">{t("assistant.title")}</h2>
            <p>{t("assistant.intro")}</p>
            {assistant.data.canStart ? <StartAssistantRun /> : null}
            {assistant.data.runs.length === 0 ? (
              <p>{t("assistant.noRuns")}</p>
            ) : (
              <div className="agents-table">
                <table data-testid="eval-assistant-runs">
                  <thead>
                    <tr>
                      <th scope="col">{t("dashboard.started")}</th>
                      <th scope="col">{t("dashboard.trigger")}</th>
                      <th scope="col">{t("dashboard.status")}</th>
                      <th scope="col">{t("dashboard.progress")}</th>
                      <th scope="col">{t("dashboard.verdict")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {assistant.data.runs.map((r) => (
                      <tr key={r.id}>
                        <td>
                          <Link href={`/admin/evaluation/runs/${r.id}`}>{format.dateTime(new Date(r.createdAt), { dateStyle: "medium", timeStyle: "short" })}</Link>
                        </td>
                        <td>{t(`trigger.${r.trigger}`)}</td>
                        <td>{t(`runStatus.${r.status}`)}</td>
                        <td data-number>{t("dashboard.progressValue", { done: String(r.done), total: String(r.total) })}</td>
                        <td>{r.verdict ? t(`verdict.${r.verdict}`) : t("none")}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        ) : null}
      </div>
    </>
  );
}
