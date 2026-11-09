import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { ChangedCase, CompareSide, CompareView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

const GUID = /^[0-9a-f-]{36}$/i;

/**
 * Two runs side by side (spec 034 US6 scenario 2): each metric by locale with the difference, the models, cost per
 * successful task, p50 and p95 latency, and the cases that went from pass to fail and back, each opening its evidence.
 */
export default async function EvaluationComparePage({ searchParams }: { searchParams: Promise<{ a?: string; b?: string }> }) {
  await evaluationSection();
  const { a, b } = await searchParams;
  if (!a || !b || !GUID.test(a) || !GUID.test(b)) notFound();
  const view = await apiGet<CompareView>(`/api/v1/evaluation/runs/compare?a=${encodeURIComponent(a)}&b=${encodeURIComponent(b)}`);
  if (!view.ok) notFound();
  const t = await getTranslations("evaluation");
  const format = await getFormatter();
  const v = view.data;
  const percent = (x: number | null) => (x === null ? t("none") : format.number(x, { style: "percent", maximumFractionDigits: 1 }));
  const signed = (x: number | null) => (x === null ? t("none") : format.number(x, { style: "percent", maximumFractionDigits: 1, signDisplay: "exceptZero" }));
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const locales = [...new Set([...Object.keys(v.a.latencyP95), ...Object.keys(v.b.latencyP95), ...Object.keys(v.a.latencyP50), ...Object.keys(v.b.latencyP50)])].sort();
  const ms = (x: number | null | undefined) => (x == null ? t("none") : t("ms", { value: format.number(x) }));
  const tokens = (x: number | null | undefined) => (x == null ? t("none") : format.number(x, { maximumFractionDigits: 0 }));
  const sides: [string, CompareSide][] = [
    ["a", v.a],
    ["b", v.b],
  ];

  function changed(id: string, title: string, list: ChangedCase[]) {
    return (
      <section className="agents-section" aria-labelledby={id}>
        <h2 id={id}>{title}</h2>
        {list.length === 0 ? (
          <p>{t("compare.noChange")}</p>
        ) : (
          <div className="agents-table">
            <table data-testid={id}>
              <thead>
                <tr>
                  <th scope="col">{t("run.input")}</th>
                  <th scope="col">{t("locale")}</th>
                  <th scope="col">{t("compare.inA")}</th>
                  <th scope="col">{t("compare.inB")}</th>
                </tr>
              </thead>
              <tbody>
                {list.map((c) => (
                  <tr key={`${c.caseInA}:${c.caseInB}`}>
                    <th scope="row">
                      <bdi>{c.input}</bdi>
                    </th>
                    <td>{t(`locales.${c.locale}`)}</td>
                    <td>
                      <Link href={`/admin/evaluation/runs/${v.a.runId}/cases/${c.caseInA}`}>{t("run.caseNumber", { n: String(c.caseInA) })}</Link>
                    </td>
                    <td>
                      <Link href={`/admin/evaluation/runs/${v.b.runId}/cases/${c.caseInB}`}>{t("run.caseNumber", { n: String(c.caseInB) })}</Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    );
  }

  return (
    <>
      <h1>{t("compare.title", { a: String(v.a.version), b: String(v.b.version) })}</h1>
      <EvaluationTabs current="agents" />
      <div className="agents">
        <p>
          <Link href={`/admin/evaluation/agents/${v.b.agentId}`}>{t("run.back")}</Link>
        </p>
        <section className="agents-section" aria-labelledby="eval-compare-sides">
          <h2 id="eval-compare-sides">{t("compare.runs")}</h2>
          <div className="agents-table">
            <table data-testid="eval-compare-sides">
              <thead>
                <tr>
                  <th scope="col">{t("compare.measure")}</th>
                  {sides.map(([key, s]) => (
                    <th scope="col" key={key}>
                      <Link href={`/admin/evaluation/runs/${s.runId}`}>{t(`compare.side_${key}`, { version: String(s.version) })}</Link>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                <tr>
                  <th scope="row">{t("dashboard.verdict")}</th>
                  {sides.map(([key, s]) => (
                    <td key={key}>{s.verdict ? t(`verdict.${s.verdict}`) : t("none")}</td>
                  ))}
                </tr>
                <tr>
                  <th scope="row">{t("compare.models")}</th>
                  {sides.map(([key, s]) => (
                    <td key={key}>
                      <bdi dir="ltr">{s.models.length > 0 ? s.models.join(", ") : t("none")}</bdi>
                    </td>
                  ))}
                </tr>
                {locales.map((l) => (
                  <tr key={`tokens:${l}`}>
                    <th scope="row">{t("compare.tokensIn", { locale: t(`locales.${l}`) })}</th>
                    {sides.map(([key, s]) => (
                      <td key={key} data-number>
                        {tokens(s.tokensPerSuccess[l])}
                      </td>
                    ))}
                  </tr>
                ))}
                {locales.map((l) => (
                  <tr key={`p50:${l}`}>
                    <th scope="row">{t("compare.p50In", { locale: t(`locales.${l}`) })}</th>
                    {sides.map(([key, s]) => (
                      <td key={key} data-number>
                        {ms(s.latencyP50[l])}
                      </td>
                    ))}
                  </tr>
                ))}
                {locales.map((l) => (
                  <tr key={`p95:${l}`}>
                    <th scope="row">{t("compare.p95In", { locale: t(`locales.${l}`) })}</th>
                    {sides.map(([key, s]) => (
                      <td key={key} data-number>
                        {ms(s.latencyP95[l])}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
        <section className="agents-section" aria-labelledby="eval-compare-metrics">
          <h2 id="eval-compare-metrics">{t("compare.metrics")}</h2>
          {v.metrics.length === 0 ? (
            <p>{t("dashboard.noResults")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="eval-compare-metrics">
                <thead>
                  <tr>
                    <th scope="col">{t("metric")}</th>
                    <th scope="col">{t("locale")}</th>
                    <th scope="col">{t("compare.side_a", { version: String(v.a.version) })}</th>
                    <th scope="col">{t("compare.side_b", { version: String(v.b.version) })}</th>
                    <th scope="col">{t("compare.delta")}</th>
                  </tr>
                </thead>
                <tbody>
                  {v.metrics.map((m) => (
                    <tr key={`${m.metric}:${m.locale}`}>
                      <th scope="row">{label(m.metric)}</th>
                      <td>{t(`locales.${m.locale}`)}</td>
                      <td data-number>{percent(m.a)}</td>
                      <td data-number>{percent(m.b)}</td>
                      <td data-number>{signed(m.delta)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
        {changed("eval-now-failing", t("compare.nowFailing"), v.nowFailing)}
        {changed("eval-now-passing", t("compare.nowPassing"), v.nowPassing)}
      </div>
    </>
  );
}
