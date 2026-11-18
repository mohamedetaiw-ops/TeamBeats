import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText, type AgentAlert, type AgentIncident, type AgentWatch } from "@/lib/agents/types";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { agentsSection } from "@/features/agents/guard";
import { severityTone } from "@/features/agents/severity";
import "@/features/agents/agents.css";

type Filters = { status?: string };

const STATUSES = ["open", "dismissed", "incident"] as const;

/**
 * The Agent Security Center (spec 027 FR-023, US6 scenarios 4 and 5, US9 scenario 3, T017): anomaly alerts, incidents
 * and the watch list of High risk agents, for security officers. The API shows only records of agents in the viewer's
 * scope and answers only when the package has the Security Center.
 */
export default async function AgentSecurityPage({ searchParams }: { searchParams: Promise<Filters> }) {
  await agentsSection();
  const filters = await searchParams;
  const status = filters.status && (STATUSES as readonly string[]).includes(filters.status) ? filters.status : "open";
  const [alerts, incidents, watch] = await Promise.all([
    apiGet<AgentAlert[]>(`/api/v1/agents/security/alerts?status=${status}&limit=100`),
    apiGet<AgentIncident[]>("/api/v1/agents/security/incidents?limit=100"),
    apiGet<AgentWatch[]>("/api/v1/agents/security/watch"),
  ]);
  const t = await getTranslations("agents.security");
  if (!alerts.ok) {
    if (alerts.status === 403) {
      return (
        <>
          <PageHeader title={t("title")} nav={<AgentTabs current="security" />} />
          <div className="agents">
            <section className="agents-section">
              <p>{t("unavailable")}</p>
            </section>
          </div>
        </>
      );
    }
    notFound();
  }
  const locale = await getLocale();
  const format = await getFormatter();
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium", timeStyle: "short" });
  return (
    <>
      <PageHeader title={t("title")} nav={<AgentTabs current="security" />} />
      <div className="agents">
        <section className="agents-section" aria-labelledby="agents-security-alerts">
          <h2 id="agents-security-alerts">{t("alerts")}</h2>
          <form className="agents-row" method="get">
            <div className="field">
              <label htmlFor="agents-security-status">{t("status")}</label>
              <select id="agents-security-status" name="status" defaultValue={status}>
                {STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {t(`alertStatus.${s}`)}
                  </option>
                ))}
              </select>
            </div>
            <Button type="submit">{t("apply")}</Button>
          </form>
          {alerts.data.length === 0 ? (
            <p>{t("noAlerts")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="agents-alerts">
                <thead>
                  <tr>
                    <th scope="col">{t("when")}</th>
                    <th scope="col">{t("agent")}</th>
                    <th scope="col">{t("rule")}</th>
                    <th scope="col">{t("severity")}</th>
                    <th scope="col">{t("matches")}</th>
                  </tr>
                </thead>
                <tbody>
                  {alerts.data.map((a) => (
                    <tr key={a.id}>
                      <td>
                        <Link href={`/admin/agents/security/alerts/${a.id}`}>{when(a.updatedAt)}</Link>
                      </td>
                      <td>
                        <bdi>{pickText(a.agentNames, locale) || t("unknownAgent")}</bdi>
                      </td>
                      <td>{t(`rules.${a.rule}`)}</td>
                      <td>
                        <Badge tone={severityTone(a.severity)}>{t(`severities.${a.severity}`)}</Badge>
                      </td>
                      <td data-number>{format.number(a.matches)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section className="agents-section" aria-labelledby="agents-security-incidents">
          <h2 id="agents-security-incidents">{t("incidents")}</h2>
          {!incidents.ok || incidents.data.length === 0 ? (
            <p>{t("noIncidents")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="agents-incidents">
                <thead>
                  <tr>
                    <th scope="col">{t("incident.title")}</th>
                    <th scope="col">{t("severity")}</th>
                    <th scope="col">{t("status")}</th>
                    <th scope="col">{t("opened")}</th>
                  </tr>
                </thead>
                <tbody>
                  {incidents.data.map((i) => (
                    <tr key={i.id}>
                      <td>
                        <Link href={`/admin/agents/security/incidents/${i.id}`}>
                          <bdi>{i.title}</bdi>
                        </Link>
                      </td>
                      <td>
                        <Badge tone={severityTone(i.severity)}>{t(`severities.${i.severity}`)}</Badge>
                      </td>
                      <td>{t(`incidentStatus.${i.status}`)}</td>
                      <td>{when(i.openedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section className="agents-section" aria-labelledby="agents-security-watch">
          <h2 id="agents-security-watch">{t("watch")}</h2>
          <p className="field-hint">{t("watchIntro")}</p>
          {!watch.ok || watch.data.length === 0 ? (
            <p>{t("noWatch")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="agents-watch">
                <thead>
                  <tr>
                    <th scope="col">{t("agent")}</th>
                    <th scope="col">{t("version")}</th>
                    <th scope="col">{t("watchedUntil")}</th>
                  </tr>
                </thead>
                <tbody>
                  {watch.data.map((w) => (
                    <tr key={w.agentId}>
                      <td>
                        <Link href={`/admin/agents/${w.agentId}`}>
                          <bdi>{pickText(w.agentNames, locale) || t("unknownAgent")}</bdi>
                        </Link>
                      </td>
                      <td data-number>{format.number(w.version)}</td>
                      <td>{format.dateTime(new Date(w.until), { dateStyle: "medium" })}</td>
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
