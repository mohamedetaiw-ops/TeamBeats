import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { DashboardView, RunSummary } from "@/lib/evaluation/types";
import { Button } from "@/components/ui/Button";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { StartRun } from "@/features/evaluation/StartRun";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

/** Columns of the trend table: the newest versions, so the table stays readable. */
const TREND_VERSIONS = 6;

/**
 * An agent's evaluation dashboard (spec 034 US6 scenarios 1 and 5): each version with its gate and headline metrics,
 * the metrics across versions as a table, the agent's runs, starting a run, and choosing two runs to compare.
 */
export default async function EvaluationDashboardPage({ params }: { params: Promise<{ id: string }> }) {
  await evaluationSection();
  const { id } = await params;
  const [dashboard, runs] = await Promise.all([
    apiGet<DashboardView>(`/api/v1/evaluation/agents/${encodeURIComponent(id)}/dashboard`),
    apiGet<RunSummary[]>(`/api/v1/evaluation/runs?agentId=${encodeURIComponent(id)}`),
  ]);
  if (!dashboard.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  const d = dashboard.data;
  const list = runs.ok ? runs.data : [];
  const percent = (v: number | null) => (v === null ? t("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const trend = d.versions.slice(0, TREND_VERSIONS);
  const rows = [...new Map(trend.flatMap((v) => v.metrics.map((m) => [`${m.metric}:${m.locale}`, m] as const))).values()].sort(
    (a, b) => a.metric.localeCompare(b.metric) || a.locale.localeCompare(b.locale),
  );
  const completed = list.filter((r) => r.status === "completed");
  const current = d.versions.find((v) => v.current);
  return (
    <>
      <h1>
        <bdi>{pickText(d.name, locale) || t("untitled")}</bdi>
      </h1>
      <EvaluationTabs current="agents" />
      <div className="agents">
        <p className="agents-row">
          <Link href={`/admin/evaluation/agents/${d.agentId}/suite`}>{t("dashboard.suite")}</Link>
          <Link href={`/admin/agents/${d.agentId}`}>{t("dashboard.agent")}</Link>
        </p>
        <section className="agents-section" aria-labelledby="eval-summary">
          <h2 id="eval-summary">{t("dashboard.summary")}</h2>
          <p data-testid="eval-summary">
            {current
              ? t("dashboard.summaryCurrent", { version: String(current.version), gate: t.has(`gate.${current.gate.status}`) ? t(`gate.${current.gate.status}`) : current.gate.status })
              : t("dashboard.summaryNone")}
          </p>
        </section>
        <section className="agents-section" aria-labelledby="eval-versions">
          <h2 id="eval-versions">{t("dashboard.versions")}</h2>
          <div className="agents-table">
            <table data-testid="eval-versions">
              <thead>
                <tr>
                  <th scope="col">{t("dashboard.version")}</th>
                  <th scope="col">{t("dashboard.gate")}</th>
                  <th scope="col">{t("dashboard.verdict")}</th>
                  <th scope="col">{t("dashboard.finished")}</th>
                  <th scope="col">{t("dashboard.run")}</th>
                </tr>
              </thead>
              <tbody>
                {d.versions.map((v) => (
                  <tr key={v.version}>
                    <th scope="row">
                      {t("dashboard.versionNumber", { number: String(v.version) })}
                      {v.current ? <span className="agents-badge">{t("dashboard.inForce")}</span> : null}
                    </th>
                    <td>{t.has(`gate.${v.gate.status}`) ? t(`gate.${v.gate.status}`) : v.gate.status}</td>
                    <td>{v.verdict ? t(`verdict.${v.verdict}`) : t("none")}</td>
                    <td>{v.finishedAt ? format.dateTime(new Date(v.finishedAt), { dateStyle: "medium", timeStyle: "short" }) : t("none")}</td>
                    <td>{v.runId ? <Link href={`/admin/evaluation/runs/${v.runId}`}>{t("dashboard.openRun")}</Link> : t("none")}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
        <section className="agents-section" aria-labelledby="eval-trend">
          <h2 id="eval-trend">{t("dashboard.trend")}</h2>
          {rows.length === 0 ? (
            <p>{t("dashboard.noResults")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="eval-trend">
                <caption>{t("dashboard.trendCaption")}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t("metric")}</th>
                    <th scope="col">{t("locale")}</th>
                    {trend.map((v) => (
                      <th scope="col" key={v.version}>
                        {t("dashboard.versionNumber", { number: String(v.version) })}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={`${r.metric}:${r.locale}`}>
                      <th scope="row">{label(r.metric)}</th>
                      <td>{t(`locales.${r.locale}`)}</td>
                      {trend.map((v) => {
                        const m = v.metrics.find((x) => x.metric === r.metric && x.locale === r.locale);
                        return (
                          <td key={v.version} data-number>
                            {m ? percent(m.value) : t("none")}
                            {m && m.status !== "pass" && m.status !== "no_threshold" ? <span className="agents-badge">{t(`metricStatus.${m.status}`)}</span> : null}
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
        <section className="agents-section" aria-labelledby="eval-runs">
          <h2 id="eval-runs">{t("dashboard.runs")}</h2>
          {d.canDesign ? <StartRun agentId={d.agentId} versions={d.versions.map((v) => v.version)} latest={d.latestVersion} /> : null}
          {list.length === 0 ? (
            <p>{t("dashboard.noRuns")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="eval-runs">
                <thead>
                  <tr>
                    <th scope="col">{t("dashboard.started")}</th>
                    <th scope="col">{t("dashboard.version")}</th>
                    <th scope="col">{t("dashboard.trigger")}</th>
                    <th scope="col">{t("dashboard.status")}</th>
                    <th scope="col">{t("dashboard.progress")}</th>
                    <th scope="col">{t("dashboard.verdict")}</th>
                  </tr>
                </thead>
                <tbody>
                  {list.map((r) => (
                    <tr key={r.id}>
                      <td>
                        <Link href={`/admin/evaluation/runs/${r.id}`}>{format.dateTime(new Date(r.createdAt), { dateStyle: "medium", timeStyle: "short" })}</Link>
                      </td>
                      <td data-number>{r.agentVersion}</td>
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
        {completed.length >= 2 ? (
          <form className="agents-section" method="get" action="/admin/evaluation/compare" aria-labelledby="eval-compare">
            <h2 id="eval-compare">{t("dashboard.compare")}</h2>
            <div className="agents-row">
              {(["a", "b"] as const).map((side, i) => (
                <div className="field" key={side}>
                  <label htmlFor={`eval-compare-${side}`}>{t(`dashboard.compare_${side}`)}</label>
                  <select id={`eval-compare-${side}`} name={side} defaultValue={completed[Math.min(i === 0 ? 1 : 0, completed.length - 1)]?.id}>
                    {completed.map((r) => (
                      <option key={r.id} value={r.id}>
                        {t("dashboard.runOption", {
                          version: String(r.agentVersion),
                          when: format.dateTime(new Date(r.finishedAt ?? r.createdAt), { dateStyle: "medium", timeStyle: "short" }),
                        })}
                      </option>
                    ))}
                  </select>
                </div>
              ))}
              <Button type="submit">{t("dashboard.compareButton")}</Button>
            </div>
          </form>
        ) : null}
      </div>
    </>
  );
}
