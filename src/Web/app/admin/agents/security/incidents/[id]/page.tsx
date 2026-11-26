import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { incidentKind, type AgentIncident } from "@/lib/agents/types";
import { Badge } from "@/components/ui/Badge";
import { PageHeader } from "@/components/ui/PageHeader";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { IncidentWork } from "@/features/agents/SecurityWork";
import { agentsSection } from "@/features/agents/guard";
import { severityTone } from "@/features/agents/severity";
import "@/features/agents/agents.css";

/** One incident (spec 027 US6 scenario 5): severity, linked runs and alerts, actions taken, the closure note and evidence. */
export default async function AgentIncidentPage({ params }: { params: Promise<{ id: string }> }) {
  await agentsSection();
  const { id } = await params;
  const incident = await apiGet<AgentIncident>(`/api/v1/agents/security/incidents/${encodeURIComponent(id)}`);
  if (!incident.ok) notFound();
  const i = incident.data;
  const t = await getTranslations("agents.security");
  const format = await getFormatter();
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium", timeStyle: "short" });
  return (
    <>
      <PageHeader title={incidentKind(i.kind) ? t(`incident.kinds.${incidentKind(i.kind)}`) : i.title} nav={<AgentTabs current="security" />} />
      <div className="agents">
        <section className="agents-section" aria-labelledby="agents-incident-facts">
          <h2 id="agents-incident-facts">{t("incident.facts")}</h2>
          <dl className="agents-pair">
            <div>
              <dt>{t("severity")}</dt>
              <dd>
                <Badge tone={severityTone(i.severity)}>{t(`severities.${i.severity}`)}</Badge>
              </dd>
            </div>
            <div>
              <dt>{t("status")}</dt>
              <dd>{t(`incidentStatus.${i.status}`)}</dd>
            </div>
            <div>
              <dt>{t("opened")}</dt>
              <dd>{when(i.openedAt)}</dd>
            </div>
            {i.closedAt ? (
              <div>
                <dt>{t("incident.closedAt")}</dt>
                <dd>{when(i.closedAt)}</dd>
              </div>
            ) : null}
            {i.agentId ? (
              <div>
                <dt>{t("agent")}</dt>
                <dd>
                  <Link href={`/admin/agents/${i.agentId}`}>{t("incident.openAgent")}</Link>
                </dd>
              </div>
            ) : null}
          </dl>
          {i.closureNote ? (
            <>
              <h3>{t("incident.closureNote")}</h3>
              <p dir="auto">{i.closureNote}</p>
            </>
          ) : null}
        </section>
        <section className="agents-section" aria-labelledby="agents-incident-actions">
          <h2 id="agents-incident-actions">{t("incident.actions")}</h2>
          {i.actions.length === 0 ? (
            <p>{t("incident.noActions")}</p>
          ) : (
            <ol className="agents-timeline">
              {i.actions.map((a) => (
                <li key={`${a.at}-${a.by}`}>
                  <p className="field-hint">{when(a.at)}</p>
                  <p dir="auto">{a.text}</p>
                </li>
              ))}
            </ol>
          )}
        </section>
        <section className="agents-section" aria-labelledby="agents-incident-links">
          <h2 id="agents-incident-links">{t("runs")}</h2>
          {i.runIds.length === 0 ? (
            <p>{t("noRuns")}</p>
          ) : (
            <ul>
              {i.runIds.map((run, n) => (
                <li key={run}>
                  <Link href={`/admin/agents/runs/${run}`}>{t("runN", { n: n + 1 })}</Link>
                </li>
              ))}
            </ul>
          )}
          {i.alertIds.length > 0 ? (
            <ul>
              {i.alertIds.map((alert, n) => (
                <li key={alert}>
                  <Link href={`/admin/agents/security/alerts/${alert}`}>{t("alertN", { n: n + 1 })}</Link>
                </li>
              ))}
            </ul>
          ) : null}
        </section>
        <IncidentWork incident={i} />
      </div>
    </>
  );
}
