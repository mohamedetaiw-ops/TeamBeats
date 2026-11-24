import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { DashboardView, DriftView, ProductionSummary } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * An agent's production quality (spec 034 US7 and US9 scenario 4): scores of sampled live runs over the window per
 * metric and language, latency, tokens and denials, the recent samples, and drift alerts with the examples behind them
 * and a link to model routing. Content of a sample shows only to those the API lets see it, while it is kept.
 */
export default async function EvaluationProductionPage({ params }: { params: Promise<{ id: string }> }) {
  await evaluationSection();
  const { id } = await params;
  const agent = encodeURIComponent(id);
  const [dashboard, production, drift] = await Promise.all([
    apiGet<DashboardView>(`/api/v1/evaluation/agents/${agent}/dashboard`),
    apiGet<ProductionSummary>(`/api/v1/evaluation/agents/${agent}/production`),
    apiGet<DriftView>(`/api/v1/evaluation/agents/${agent}/drift`),
  ]);
  if (!dashboard.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  const d = dashboard.data;
  const percent = (v: number | null) => (v === null ? t("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const ms = (v: number | null) => (v === null ? t("none") : t("ms", { value: format.number(Math.round(v)) }));
  return (
    <>
      <AdminHeader section="evaluation" title={pickText(d.name, locale) || t("untitled")} nav={<EvaluationTabs current="agents" />} />
      <div className="agents">
        <p className="agents-row">
          <Link href={`/admin/evaluation/agents/${d.agentId}`}>{t("productionView.back")}</Link>
          <Link href={`/admin/evaluation/agents/${d.agentId}/comparisons`}>{t("dashboard.comparisons")}</Link>
        </p>
        {!production.ok ? (
          <p role="status">{t("productionView.unavailable")}</p>
        ) : (
          <>
            <section className="agents-section" aria-labelledby="eval-production-summary">
              <h2 id="eval-production-summary">{t("productionView.summary", { days: String(production.data.windowDays) })}</h2>
              <p data-testid="eval-production-counts">
                {t("productionView.counts", { samples: format.number(production.data.samples), scored: format.number(production.data.scored) })}
              </p>
              <p className="agents-row">
                <span>
                  {t("productionView.latency")} {ms(production.data.latencyP95Ms)}
                </span>
                <span>
                  {t("productionView.tokens")} {production.data.averageTokens === null ? t("none") : format.number(Math.round(production.data.averageTokens))}
                </span>
                <span>
                  {t("productionView.denials")} {format.number(production.data.denials)}
                </span>
              </p>
              {production.data.metrics.length === 0 ? <p>{t("productionView.noScores")}</p> : null}
              {production.data.metrics.length > 0 ? (
                <div className="agents-table">
                  <table data-testid="eval-production-metrics">
                    <thead>
                      <tr>
                        <th scope="col">{t("metric")}</th>
                        <th scope="col">{t("locale")}</th>
                        <th scope="col">{t("productionView.value")}</th>
                        <th scope="col">{t("productionView.samples")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {production.data.metrics.map((m) => (
                        <tr key={`${m.key}:${m.locale}`}>
                          <th scope="row">{label(m.key)}</th>
                          <td>{t(`locales.${m.locale}`)}</td>
                          <td data-number>{percent(m.value)}</td>
                          <td data-number>{format.number(m.samples)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
            </section>
            <section className="agents-section" aria-labelledby="eval-production-recent">
              <h2 id="eval-production-recent">{t("productionView.recent")}</h2>
              {production.data.recent.length === 0 ? <p>{t("productionView.noSamples")}</p> : null}
              {production.data.recent.length > 0 ? (
                <div className="agents-table">
                  <table data-testid="eval-production-samples">
                    <thead>
                      <tr>
                        <th scope="col">{t("productionView.sampledAt")}</th>
                        <th scope="col">{t("dashboard.version")}</th>
                        <th scope="col">{t("locale")}</th>
                        <th scope="col">{t("productionView.mode")}</th>
                        <th scope="col">{t("productionView.outcome")}</th>
                        <th scope="col">{t("productionView.latency")}</th>
                        <th scope="col">{t("productionView.scores")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {production.data.recent.map((s) => (
                        <tr key={s.id}>
                          <th scope="row">{format.dateTime(new Date(s.sampledAt), { dateStyle: "medium", timeStyle: "short" })}</th>
                          <td>{t("dashboard.versionNumber", { number: String(s.agentVersion) })}</td>
                          <td>{t.has(`locales.${s.language}`) ? t(`locales.${s.language}`) : s.language}</td>
                          <td>{s.status === "scored" ? t(`productionView.modes.${s.mode}`) : t("productionView.queued")}</td>
                          <td>{s.outcome && t.has(`outcome.${s.outcome}`) ? t(`outcome.${s.outcome}`) : t("none")}</td>
                          <td data-number>{ms(s.latencyMs)}</td>
                          <td>
                            {Object.entries(s.metrics)
                              .filter(([, m]) => m.value !== null)
                              .map(([k, m]) => `${label(k)}: ${percent(m.value)}`)
                              .join(t("listSeparator")) || t("none")}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
            </section>
          </>
        )}
        {drift.ok ? (
          <section className="agents-section" aria-labelledby="eval-drift">
            <h2 id="eval-drift">{t("drift.title")}</h2>
            <p>{t("drift.intro", { days: String(drift.data.windowDays), samples: String(drift.data.minSamples) })}</p>
            {drift.data.alerts.length === 0 ? <p data-testid="eval-drift-none">{t("drift.none")}</p> : null}
            {drift.data.alerts.map((a) => (
              <article key={a.id} className="agents-card" data-testid="eval-drift-alert" aria-labelledby={`eval-drift-${a.id}`}>
                <h3 id={`eval-drift-${a.id}`}>{t("drift.alertTitle", { metric: label(a.metric), language: t.has(`locales.${a.locale}`) ? t(`locales.${a.locale}`) : a.locale })}</h3>
                <p className="agents-row">
                  <span className="agents-badge">{t(`drift.statuses.${a.status}`)}</span>
                  <span>{t("dashboard.versionNumber", { number: String(a.agentVersion) })}</span>
                  <span>{format.dateTime(new Date(a.detectedAt), { dateStyle: "medium" })}</span>
                </p>
                <p>{t("drift.values", { value: percent(a.value), baseline: percent(a.baseline), threshold: percent(a.threshold), samples: format.number(a.samples) })}</p>
                {a.models.length > 0 ? (
                  <p>
                    {t("drift.models")} <bdi dir="ltr">{a.models.join(t("listSeparator"))}</bdi>
                  </p>
                ) : null}
                {a.examples.length > 0 ? <p>{t("drift.examples", { count: a.examples.length })}</p> : null}
              </article>
            ))}
            <p>
              <Link href={drift.data.routingLink}>{t("drift.routing")}</Link>
            </p>
          </section>
        ) : null}
      </div>
    </>
  );
}
