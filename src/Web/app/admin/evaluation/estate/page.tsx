import Link from "next/link";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { EstateTrendsView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Trend benchmarks across the agent estate (spec 034 R3, US6 scenarios 1, 4 and 5 at estate level): a text summary, each
 * agent's latest evaluated version against the one before with the drops beyond the noise margin, and the monthly estate
 * benchmarks per metric and locale as a table. The API counts only the agents the person may read.
 */
export default async function EvaluationEstatePage() {
  await evaluationSection();
  const trends = await apiGet<EstateTrendsView>("/api/v1/evaluation/estate/trends");
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  const percent = (v: number | null) => (v === null ? t("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const language = (l: string) => (t.has(`locales.${l}`) ? t(`locales.${l}`) : l);
  const monthLabel = (m: string) => format.dateTime(new Date(`${m}-01T00:00:00Z`), { month: "short", year: "numeric", timeZone: "UTC" });
  const header = <AdminHeader section="evaluation" title={t("estate.title")} nav={<EvaluationTabs current="estate" />} />;
  if (!trends.ok) {
    return (
      <>
        {header}
        <div className="agents">
          <p role="status">{trends.status === 403 ? t("estate.unavailable") : t("failed")}</p>
        </div>
      </>
    );
  }
  const view = trends.data;
  const evaluated = view.agents.filter((a) => a.version !== null);
  const regressed = view.agents.filter((a) => a.regressed);
  // Only months with a result, so the table stays readable.
  const months = view.months.filter((m) => view.benchmarks.some((s) => s.points.some((p) => p.month === m)));
  return (
    <>
      {header}
      <div className="agents">
        <p>{t("estate.intro", { margin: percent(view.noiseMargin) })}</p>
        <section className="agents-section" aria-labelledby="estate-summary">
          <h2 id="estate-summary">{t("estate.summary")}</h2>
          <p data-testid="estate-summary">
            {evaluated.length === 0
              ? t("estate.summaryNone")
              : t("estate.summaryText", { agents: String(evaluated.length), regressed: String(regressed.length) })}
          </p>
        </section>
        <section className="agents-section" aria-labelledby="estate-agents">
          <h2 id="estate-agents">{t("estate.agents")}</h2>
          {view.agents.length === 0 ? (
            <p>{t("estate.noAgents")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="estate-agents">
                <thead>
                  <tr>
                    <th scope="col">{t("estate.agent")}</th>
                    <th scope="col">{t("estate.version")}</th>
                    <th scope="col">{t("estate.previous")}</th>
                    <th scope="col">{t("estate.verdict")}</th>
                    <th scope="col">{t("estate.drops")}</th>
                  </tr>
                </thead>
                <tbody>
                  {view.agents.map((a) => {
                    const drops = a.metrics.filter((m) => m.regressed);
                    return (
                      <tr key={a.agentId}>
                        <th scope="row">
                          <Link href={`/admin/evaluation/agents/${a.agentId}`}>
                            <bdi>{pickText(a.name, locale) || t("untitled")}</bdi>
                          </Link>
                        </th>
                        <td>{a.version === null ? t("none") : t("dashboard.versionNumber", { number: String(a.version) })}</td>
                        <td>{a.previousVersion === null ? t("none") : t("dashboard.versionNumber", { number: String(a.previousVersion) })}</td>
                        <td>{a.verdict ? t(`verdict.${a.verdict}`) : t("none")}</td>
                        <td>
                          {drops.length === 0
                            ? t("estate.noDrop")
                            : drops
                                .map((m) => t("estate.drop", { metric: label(m.metric), language: language(m.locale), from: percent(m.previous), to: percent(m.value) }))
                                .join(t("listSeparator"))}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>
        <section className="agents-section" aria-labelledby="estate-benchmarks">
          <h2 id="estate-benchmarks">{t("estate.benchmarks")}</h2>
          {view.benchmarks.length === 0 ? (
            <p>{t("dashboard.noResults")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="estate-benchmarks">
                <caption>{t("estate.benchmarksCaption")}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t("metric")}</th>
                    <th scope="col">{t("locale")}</th>
                    {months.map((m) => (
                      <th scope="col" key={m}>
                        {monthLabel(m)}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {view.benchmarks.map((s) => (
                    <tr key={`${s.metric}:${s.locale}`}>
                      <th scope="row">{label(s.metric)}</th>
                      <td>{language(s.locale)}</td>
                      {months.map((m) => {
                        const point = s.points.find((p) => p.month === m);
                        return (
                          <td key={m} data-number>
                            {point ? t("estate.point", { value: percent(point.value), agents: String(point.agents) }) : t("none")}
                          </td>
                        );
                      })}
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
