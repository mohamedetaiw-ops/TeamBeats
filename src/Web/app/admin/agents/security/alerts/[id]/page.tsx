import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText, type AgentAlert } from "@/lib/agents/types";
import { Badge } from "@/components/ui/Badge";
import { PageHeader } from "@/components/ui/PageHeader";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { AlertActions } from "@/features/agents/SecurityWork";
import { agentsSection } from "@/features/agents/guard";
import { severityTone } from "@/features/agents/severity";
import "@/features/agents/agents.css";

/** One anomaly alert (spec 027 US6 scenario 4): what fired, for which agent, its runs, and the work on it. */
export default async function AgentAlertPage({ params }: { params: Promise<{ id: string }> }) {
  await agentsSection();
  const { id } = await params;
  const alert = await apiGet<AgentAlert>(`/api/v1/agents/security/alerts/${encodeURIComponent(id)}`);
  if (!alert.ok) notFound();
  const a = alert.data;
  const t = await getTranslations("agents.security");
  const locale = await getLocale();
  const format = await getFormatter();
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium", timeStyle: "short" });
  return (
    <>
      <PageHeader title={t(`rules.${a.rule}`)} nav={<AgentTabs current="security" />} />
      <div className="agents">
        <section className="agents-section" aria-labelledby="agents-alert-facts">
          <h2 id="agents-alert-facts">{t("alert.facts")}</h2>
          <dl className="agents-pair">
            <div>
              <dt>{t("agent")}</dt>
              <dd>
                <Link href={`/admin/agents/${a.agentId}`}>
                  <bdi>{pickText(a.agentNames, locale) || t("unknownAgent")}</bdi>
                </Link>
              </dd>
            </div>
            <div>
              <dt>{t("severity")}</dt>
              <dd>
                <Badge tone={severityTone(a.severity)}>{t(`severities.${a.severity}`)}</Badge>
              </dd>
            </div>
            <div>
              <dt>{t("status")}</dt>
              <dd>{t(`alertStatus.${a.status}`)}</dd>
            </div>
            <div>
              <dt>{t("matches")}</dt>
              <dd>{format.number(a.matches)}</dd>
            </div>
            <div>
              <dt>{t("firstSeen")}</dt>
              <dd>{when(a.createdAt)}</dd>
            </div>
            <div>
              <dt>{t("lastSeen")}</dt>
              <dd>{when(a.updatedAt)}</dd>
            </div>
          </dl>
          <p>{t(`ruleHelp.${a.rule}`)}</p>
        </section>
        <section className="agents-section" aria-labelledby="agents-alert-runs">
          <h2 id="agents-alert-runs">{t("runs")}</h2>
          {a.runIds.length === 0 ? (
            <p>{t("noRuns")}</p>
          ) : (
            <ul>
              {a.runIds.map((run, n) => (
                <li key={run}>
                  <Link href={`/admin/agents/runs/${run}`}>{t("runN", { n: n + 1 })}</Link>
                </li>
              ))}
            </ul>
          )}
        </section>
        <AlertActions alert={a} />
      </div>
    </>
  );
}
