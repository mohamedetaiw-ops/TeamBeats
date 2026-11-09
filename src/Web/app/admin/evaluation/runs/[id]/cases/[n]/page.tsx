import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { CaseResult, Persona } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

/**
 * One case of a run with its evidence (spec 034 US6 scenario 3): the input, what was expected, the answer as the
 * logging rules keep it, each metric, the judge's verdicts and reasons, and the link to the execution record, which
 * holds the trajectory and the sources.
 */
export default async function EvaluationCasePage({ params }: { params: Promise<{ id: string; n: string }> }) {
  await evaluationSection();
  const { id, n } = await params;
  if (!/^\d{1,6}$/.test(n)) notFound();
  const [item, personas] = await Promise.all([
    apiGet<CaseResult>(`/api/v1/evaluation/runs/${encodeURIComponent(id)}/cases/${n}`),
    apiGet<Persona[]>("/api/v1/evaluation/personas"),
  ]);
  if (!item.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  const c = item.data;
  const persona = (personas.ok ? personas.data : []).find((p) => p.id === c.personaId);
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const e = c.expect;
  const expectations: [string, string[]][] = [
    ["answer", e.answer ? [e.answer] : []],
    ["keyFacts", e.keyFacts ?? []],
    ["sources", (e.sources ?? []).map((s) => `${s.source}:${s.itemId}`)],
    ["forbiddenSources", (e.forbiddenSources ?? []).map((s) => `${s.source}:${s.itemId}`)],
    ["tools", (e.tools ?? []).map((x) => (x.parameters && Object.keys(x.parameters).length > 0 ? `${x.tool} ${JSON.stringify(x.parameters)}` : (x.tool ?? "")))],
    ["forbiddenTools", e.forbiddenTools ?? []],
  ];
  const flags = [e.toolOrder ? t("case.toolOrder") : null, e.refusal ? t("case.refusal") : null, e.escalation ? t("case.escalation") : null].filter(Boolean);
  return (
    <>
      <h1>{t("case.title", { n: String(c.n) })}</h1>
      <EvaluationTabs current="agents" />
      <div className="agents">
        <p>
          <Link href={`/admin/evaluation/runs/${id}`}>{t("case.back")}</Link>
        </p>
        <section className="agents-section" aria-labelledby="eval-case-input">
          <h2 id="eval-case-input">{t("case.input")}</h2>
          <p className="agents-answer">
            <bdi>{c.input}</bdi>
          </p>
          <dl className="agents-pair">
            <div>
              <dt>{t("locale")}</dt>
              <dd>{t(`locales.${c.locale}`)}</dd>
            </div>
            <div>
              <dt>{t("case.persona")}</dt>
              <dd>
                <bdi>{persona ? pickText(persona.names, locale) : t("case.personaHidden")}</bdi>
              </dd>
            </div>
            <div>
              <dt>{t("run.severity")}</dt>
              <dd>{t(`severity.${c.severity}`)}</dd>
            </div>
            <div>
              <dt>{t("dashboard.status")}</dt>
              <dd>{t(`caseStatus.${c.status}`)}</dd>
            </div>
            <div>
              <dt>{t("run.result")}</dt>
              <dd data-testid="eval-case-result">{c.status === "done" || c.status === "error" ? (c.failed ? t("run.caseFailed") : t("run.casePassed")) : t("none")}</dd>
            </div>
          </dl>
        </section>
        {c.context && c.context.length > 0 ? (
          <section className="agents-section" aria-labelledby="eval-case-context">
            <h2 id="eval-case-context">{t("case.context")}</h2>
            <ol data-testid="eval-case-context">
              {c.context.map((p, i) => (
                <li key={i}>
                  <strong>
                    <bdi>{p.title}</bdi>
                  </strong>
                  <p className="agents-answer">
                    <bdi>{p.text}</bdi>
                  </p>
                </li>
              ))}
            </ol>
          </section>
        ) : null}
        <section className="agents-section" aria-labelledby="eval-case-expect">
          <h2 id="eval-case-expect">{t("case.expected")}</h2>
          <dl>
            {expectations
              .filter(([, values]) => values.length > 0)
              .map(([key, values]) => (
                <div key={key}>
                  <dt>{t(`case.expect.${key}`)}</dt>
                  {values.map((v) => (
                    <dd key={v} className="agents-answer">
                      <bdi>{v}</bdi>
                    </dd>
                  ))}
                </div>
              ))}
          </dl>
          {flags.length > 0 ? <p>{flags.join(t("listSeparator"))}</p> : null}
        </section>
        <section className="agents-section" aria-labelledby="eval-case-answer">
          <h2 id="eval-case-answer">{t("case.answer")}</h2>
          {c.answer ? (
            <p className="agents-answer" data-testid="eval-case-answer">
              <bdi>{c.answer}</bdi>
            </p>
          ) : (
            <p>{t("case.noAnswer")}</p>
          )}
          {c.error ? <p className="notice notice-error">{t.has(`case.errors.${c.error}`) ? t(`case.errors.${c.error}`) : t("case.errors.other")}</p> : null}
          <dl className="agents-pair">
            <div>
              <dt>{t("case.outcome")}</dt>
              <dd>{c.outcome ? (t.has(`outcome.${c.outcome}`) ? t(`outcome.${c.outcome}`) : c.outcome) : t("none")}</dd>
            </div>
            <div>
              <dt>{t("case.model")}</dt>
              <dd>
                <bdi dir="ltr">{c.model ?? t("none")}</bdi>
              </dd>
            </div>
            <div>
              <dt>{t("case.latency")}</dt>
              <dd>{c.latencyMs === null ? t("none") : t("ms", { value: format.number(c.latencyMs) })}</dd>
            </div>
            <div>
              <dt>{t("case.tokens")}</dt>
              <dd>{c.tokens === null ? t("none") : format.number(c.tokens)}</dd>
            </div>
          </dl>
          {c.agentRunId ? (
            <p>
              <Link href={`/admin/agents/runs/${c.agentRunId}`} data-testid="eval-case-record">
                {t("case.record")}
              </Link>
            </p>
          ) : null}
        </section>
        {c.metrics && Object.keys(c.metrics).length > 0 ? (
          <section className="agents-section" aria-labelledby="eval-case-metrics">
            <h2 id="eval-case-metrics">{t("case.metrics")}</h2>
            <div className="agents-table">
              <table data-testid="eval-case-metrics">
                <thead>
                  <tr>
                    <th scope="col">{t("metric")}</th>
                    <th scope="col">{t("run.value")}</th>
                    <th scope="col">{t("case.state")}</th>
                  </tr>
                </thead>
                <tbody>
                  {Object.entries(c.metrics).map(([key, m]) => (
                    <tr key={key}>
                      <th scope="row">{label(key)}</th>
                      <td data-number>{m.value === null ? t("none") : format.number(m.value, { maximumFractionDigits: 3 })}</td>
                      <td>{t(`caseMetricState.${m.state}`)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        ) : null}
        {c.verdicts && c.verdicts.length > 0 ? (
          <section className="agents-section" aria-labelledby="eval-case-judge">
            <h2 id="eval-case-judge">{t("case.judge")}</h2>
            <div className="agents-table">
              <table data-testid="eval-case-judge">
                <thead>
                  <tr>
                    <th scope="col">{t("metric")}</th>
                    <th scope="col">{t("case.verdict")}</th>
                    <th scope="col">{t("case.reason")}</th>
                    <th scope="col">{t("case.model")}</th>
                    <th scope="col">{t("case.rubric")}</th>
                  </tr>
                </thead>
                <tbody>
                  {c.verdicts.map((v) => (
                    <tr key={v.metric}>
                      <th scope="row">{label(v.metric)}</th>
                      <td>{t.has(`judgeVerdict.${v.verdict}`) ? t(`judgeVerdict.${v.verdict}`) : v.verdict}</td>
                      <td>
                        <bdi>{v.reason}</bdi>
                      </td>
                      <td>
                        <bdi dir="ltr">{v.model}</bdi>
                      </td>
                      <td>
                        <bdi dir="ltr">{v.rubric}</bdi>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        ) : null}
      </div>
    </>
  );
}
