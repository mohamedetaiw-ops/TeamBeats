import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { EvalAgentRow } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

/** The evaluation overview (spec 034 US6): the agents whose evaluation the viewer may read, with the gate of their latest version. */
export default async function EvaluationOverviewPage() {
  await evaluationSection();
  const agents = await apiGet<EvalAgentRow[]>("/api/v1/evaluation/agents");
  if (!agents.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
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
      </div>
    </>
  );
}
