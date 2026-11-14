import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pickText, type AgentSummary, type TemplateView } from "@/lib/agents/types";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { TemplateLibrary } from "@/features/agents/TemplateLibrary";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";
import { StateView } from "@/components/ui/StateView";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * The agent registry and template library (spec 020 US1, T014): the agents the person may see with status, risk class
 * and version, and the launch templates of the package, marked not ready when a tool is missing or blocked by policy.
 */
export default async function AgentsPage() {
  await agentsSection();
  const [agents, templates, units] = await Promise.all([
    apiGet<{ agents: AgentSummary[] }>("/api/v1/agents"),
    apiGet<{ templates: TemplateView[] }>("/api/v1/agents/templates"),
    listUnits(),
  ]);
  if (!agents.ok) notFound();
  const t = await getTranslations("agents");
  const locale = await getLocale();
  return (
    <>
      <AdminHeader section="agents" nav={<AgentTabs current="agents" />} />
      <div className="agents">
        <section className="agents-section" aria-labelledby="agents-registry">
          <h2 id="agents-registry">{t("registry.heading")}</h2>
          {agents.data.agents.length === 0 ? (
            <StateView kind="empty" title={t("registry.empty")} />
          ) : (
            <div className="agents-table">
              <table data-testid="agents-registry">
                <thead>
                  <tr>
                    <th scope="col">{t("registry.name")}</th>
                    <th scope="col">{t("registry.status")}</th>
                    <th scope="col">{t("registry.risk")}</th>
                    <th scope="col">{t("registry.version")}</th>
                  </tr>
                </thead>
                <tbody>
                  {agents.data.agents.map((a) => (
                    <tr key={a.id}>
                      <td>
                        <Link href={`/admin/agents/${a.id}`}>
                          <bdi>{pickText(a.name, locale)}</bdi>
                        </Link>
                      </td>
                      <td>{t(`status.${a.status}`)}</td>
                      <td>{t(`risk.${a.riskClass}`)}</td>
                      <td data-number>{a.currentVersion ?? t("registry.notPublished")}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
        {templates.ok ? <TemplateLibrary templates={templates.data.templates} units={units.ok ? units.data : []} /> : null}
      </div>
    </>
  );
}
