import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { AgentSuites, CaseResult, RunDetail } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { RunLive } from "@/features/evaluation/RunLive";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

/** Characters of a case input shown in the cases table; the case page shows all of it. */
const INPUT_PREVIEW = 120;

/**
 * One evaluation run (spec 034 US3 scenario 7 and US4): progress while it runs, then each metric per locale against its
 * threshold with severity and the level the rule came from, the failures that decide the verdict, latency, cost,
 * regressions against the baseline, and every case with a link to its evidence.
 */
export default async function EvaluationRunPage({ params }: { params: Promise<{ id: string }> }) {
  await evaluationSection();
  const { id } = await params;
  const [detail, cases] = await Promise.all([
    apiGet<RunDetail>(`/api/v1/evaluation/runs/${encodeURIComponent(id)}`),
    apiGet<CaseResult[]>(`/api/v1/evaluation/runs/${encodeURIComponent(id)}/cases`),
  ]);
  if (!detail.ok) notFound();
  const named = await apiGet<AgentSuites>(`/api/v1/evaluation/agents/${encodeURIComponent(detail.data.run.agentId)}/suites`);
  const names = new Map((named.ok ? named.data.inherited : []).map((s) => [s.key, s.names]));
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  const { run, result, suites } = detail.data;
  const list = cases.ok ? cases.data : [];
  const active = run.status === "queued" || run.status === "running";
  const percent = (v: number | null) => (v === null ? t("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const suiteName = (key: string) => (key === "agent" ? t("suiteOwn") : pickText(names.get(key), locale) || key);
  const failures = result
    ? ([
        ["critical", result.criticalFailures],
        ["blocking", result.blockingFailures],
        ["incomplete", result.incomplete],
        ["warnings", result.warnings],
      ] as const)
    : [];
  const locales = result ? [...new Set([...Object.keys(result.latencyP95), ...Object.keys(result.tokensPerSuccess)])].sort() : [];
  return (
    <>
      <h1>{t("run.title", { version: String(run.agentVersion) })}</h1>
      <EvaluationTabs current="agents" />
      <div className="agents">
        <p>
          <Link href={`/admin/evaluation/agents/${run.agentId}`}>{t("run.back")}</Link>
        </p>
        <section className="agents-section" aria-labelledby="eval-run-status" aria-live="polite">
          <h2 id="eval-run-status">{t("run.status")}</h2>
          <dl className="agents-pair">
            <div>
              <dt>{t("dashboard.trigger")}</dt>
              <dd>{t(`trigger.${run.trigger}`)}</dd>
            </div>
            <div>
              <dt>{t("dashboard.status")}</dt>
              <dd data-testid="eval-run-status">{t(`runStatus.${run.status}`)}</dd>
            </div>
            <div>
              <dt>{t("dashboard.progress")}</dt>
              <dd>
                <progress max={Math.max(run.total, 1)} value={run.done} aria-label={t("dashboard.progress")} />{" "}
                {t("dashboard.progressValue", { done: String(run.done), total: String(run.total) })}
              </dd>
            </div>
            <div>
              <dt>{t("run.errors")}</dt>
              <dd>{format.number(run.errors)}</dd>
            </div>
            <div>
              <dt>{t("dashboard.verdict")}</dt>
              <dd data-testid="eval-run-verdict">{run.verdict ? t(`verdict.${run.verdict}`) : t("none")}</dd>
            </div>
          </dl>
          <RunLive runId={run.id} active={active} canCancel={detail.data.canCancel} />
        </section>
        {failures.some(([, l]) => l.length > 0) ? (
          <section className="agents-section" aria-labelledby="eval-run-failures">
            <h2 id="eval-run-failures">{t("run.failures")}</h2>
            {failures.map(([kind, l]) =>
              l.length > 0 ? (
                <div key={kind}>
                  <h3>{t(`run.failureLists.${kind}`)}</h3>
                  <ul data-testid={`eval-run-${kind}`}>
                    {l.map((f) => (
                      <li key={f}>
                        <bdi dir="ltr">{f}</bdi>
                      </li>
                    ))}
                  </ul>
                </div>
              ) : null,
            )}
          </section>
        ) : null}
        {result
          ? result.suites.map((s) => (
              <section className="agents-section" key={s.key} aria-labelledby={`eval-suite-${s.key}`}>
                <h2 id={`eval-suite-${s.key}`}>{suiteName(s.key)}</h2>
                <div className="agents-table">
                  <table data-testid={`eval-scores-${s.key}`}>
                    <thead>
                      <tr>
                        <th scope="col">{t("metric")}</th>
                        <th scope="col">{t("locale")}</th>
                        <th scope="col">{t("run.value")}</th>
                        <th scope="col">{t("run.threshold")}</th>
                        <th scope="col">{t("run.severity")}</th>
                        <th scope="col">{t("run.level")}</th>
                        <th scope="col">{t("run.cases")}</th>
                        <th scope="col">{t("run.failing")}</th>
                        <th scope="col">{t("run.result")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {s.metrics.flatMap((m) =>
                        m.locales.map((l) => (
                          <tr key={`${m.key}:${l.locale}`}>
                            <th scope="row">{label(m.key)}</th>
                            <td>{t(`locales.${l.locale}`)}</td>
                            <td data-number>{percent(l.value)}</td>
                            <td data-number>{m.threshold === null ? t("none") : percent(m.threshold)}</td>
                            <td>{m.severity ? t(`severity.${m.severity}`) : t("none")}</td>
                            <td>{m.thresholdLevel ? t(`level.${m.thresholdLevel}`) : t("none")}</td>
                            <td data-number>{format.number(l.cases)}</td>
                            <td data-number>{format.number(l.failures)}</td>
                            <td>{t(`metricStatus.${l.status}`)}</td>
                          </tr>
                        )),
                      )}
                    </tbody>
                  </table>
                </div>
                {Object.keys(s.weightedScore).length > 0 ? (
                  <p>
                    {t("run.weighted")}{" "}
                    {Object.entries(s.weightedScore)
                      .map(([l, v]) => `${t(`locales.${l}`)}: ${percent(v)}`)
                      .join(t("listSeparator"))}
                  </p>
                ) : null}
              </section>
            ))
          : null}
        {result && locales.length > 0 ? (
          <section className="agents-section" aria-labelledby="eval-run-cost">
            <h2 id="eval-run-cost">{t("run.costLatency")}</h2>
            <div className="agents-table">
              <table>
                <thead>
                  <tr>
                    <th scope="col">{t("locale")}</th>
                    <th scope="col">{t("compare.p95")}</th>
                    <th scope="col">{t("compare.tokens")}</th>
                  </tr>
                </thead>
                <tbody>
                  {locales.map((l) => (
                    <tr key={l}>
                      <th scope="row">{t(`locales.${l}`)}</th>
                      <td data-number>{result.latencyP95[l] == null ? t("none") : t("ms", { value: format.number(result.latencyP95[l]!) })}</td>
                      <td data-number>{result.tokensPerSuccess[l] == null ? t("none") : format.number(result.tokensPerSuccess[l]!, { maximumFractionDigits: 0 })}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        ) : null}
        {result && result.regressions.length > 0 ? (
          <section className="agents-section" aria-labelledby="eval-run-regressions">
            <h2 id="eval-run-regressions">{t("run.regressions")}</h2>
            <div className="agents-table">
              <table data-testid="eval-run-regressions">
                <thead>
                  <tr>
                    <th scope="col">{t("metric")}</th>
                    <th scope="col">{t("locale")}</th>
                    <th scope="col">{t("run.baseline")}</th>
                    <th scope="col">{t("run.value")}</th>
                    <th scope="col">{t("run.noise")}</th>
                    <th scope="col">{t("run.cases")}</th>
                  </tr>
                </thead>
                <tbody>
                  {result.regressions.map((r) => (
                    <tr key={`${r.suite}:${r.metric}:${r.locale}`}>
                      <th scope="row">{label(r.metric)}</th>
                      <td>{t(`locales.${r.locale}`)}</td>
                      <td data-number>{percent(r.baseline)}</td>
                      <td data-number>{percent(r.value)}</td>
                      <td data-number>{percent(r.noise)}</td>
                      <td data-number>{format.number(r.cases)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        ) : null}
        <section className="agents-section" aria-labelledby="eval-run-suites">
          <h2 id="eval-run-suites">{t("run.tested")}</h2>
          <ul>
            {suites.map((s) => (
              <li key={s.key}>
                {t("run.suiteVersion", { suite: suiteName(s.key), version: String(s.version) })}
                {s.datasets.length > 0
                  ? ` ${t("run.datasetVersions", { versions: s.datasets.map((d) => String(d.version)).join(t("listSeparator")) })}`
                  : null}
              </li>
            ))}
          </ul>
        </section>
        <section className="agents-section" aria-labelledby="eval-run-cases">
          <h2 id="eval-run-cases">{t("run.caseList")}</h2>
          {list.length === 0 ? (
            <p>{t("run.noCases")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="eval-run-cases">
                <thead>
                  <tr>
                    <th scope="col">{t("run.case")}</th>
                    <th scope="col">{t("run.suite")}</th>
                    <th scope="col">{t("locale")}</th>
                    <th scope="col">{t("run.input")}</th>
                    <th scope="col">{t("dashboard.status")}</th>
                    <th scope="col">{t("run.result")}</th>
                  </tr>
                </thead>
                <tbody>
                  {list.map((c) => (
                    <tr key={c.n}>
                      <th scope="row">
                        <Link href={`/admin/evaluation/runs/${run.id}/cases/${c.n}`}>{t("run.caseNumber", { n: String(c.n) })}</Link>
                      </th>
                      <td>{suiteName(c.suite)}</td>
                      <td>{t(`locales.${c.locale}`)}</td>
                      <td>
                        <bdi>{c.input.length > INPUT_PREVIEW ? `${c.input.slice(0, INPUT_PREVIEW)}…` : c.input}</bdi>
                      </td>
                      <td>{t(`caseStatus.${c.status}`)}</td>
                      <td>{c.status === "done" || c.status === "error" ? (c.failed ? t("run.caseFailed") : t("run.casePassed")) : t("none")}</td>
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
