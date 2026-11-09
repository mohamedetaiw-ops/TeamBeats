import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { CalibrationLabel, RubricState } from "@/lib/evaluation/types";
import { CalibrationLabels, CalibrationStart } from "@/features/evaluation/CalibrationPanel";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

const METRIC = /^[a-z_]{1,60}$/;

/**
 * Judge calibration (spec 034 US8 scenarios 1 and 4): each judged rubric with its state, the labels people gave per
 * language, the latest agreement with the judge, and for one chosen rubric its calibration set. A rubric that agrees
 * less than the target is demoted, so its blocking thresholds count as warnings until it is calibrated again.
 */
export default async function EvaluationCalibrationPage({ searchParams }: { searchParams: Promise<{ rubric?: string }> }) {
  await evaluationSection();
  const { rubric } = await searchParams;
  const rubrics = await apiGet<RubricState[]>("/api/v1/evaluation/calibration");
  if (!rubrics.ok) notFound();
  const t = await getTranslations("evaluation");
  const format = await getFormatter();
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const chosen = rubric && METRIC.test(rubric) ? rubrics.data.find((r) => r.metric === rubric) : undefined;
  const labels = chosen ? await apiGet<CalibrationLabel[]>(`/api/v1/evaluation/calibration/${chosen.metric}/labels`) : null;
  const percent = (v: number | null) => (v === null ? t("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  return (
    <>
      <h1>{t("calibration.title")}</h1>
      <EvaluationTabs current="calibration" />
      <div className="agents">
        <p>{t("calibration.intro")}</p>
        <section className="agents-section" aria-labelledby="eval-rubrics">
          <h2 id="eval-rubrics">{t("calibration.rubrics")}</h2>
          <div className="agents-table">
            <table data-testid="eval-rubrics">
              <thead>
                <tr>
                  <th scope="col">{t("calibration.rubric")}</th>
                  <th scope="col">{t("calibration.state")}</th>
                  <th scope="col">{t("calibration.labels")}</th>
                  <th scope="col">{t("calibration.agreement")}</th>
                  <th scope="col">{t("calibration.lastRun")}</th>
                </tr>
              </thead>
              <tbody>
                {rubrics.data.map((r) => (
                  <tr key={r.metric}>
                    <th scope="row">
                      <Link href={`/admin/evaluation/calibration?rubric=${r.metric}`} aria-current={chosen?.metric === r.metric ? "page" : undefined}>
                        {label(r.metric)}
                      </Link>
                    </th>
                    <td data-testid={`eval-rubric-state-${r.metric}`}>{t(`calibration.states.${r.state}`)}</td>
                    <td>
                      {Object.entries(r.labels)
                        .map(([l, n]) => `${t(`locales.${l}`)}: ${format.number(n)}`)
                        .join(t("listSeparator")) || t("none")}
                    </td>
                    <td>
                      {r.latest && r.latest.agreement.length > 0
                        ? r.latest.agreement
                            .map((a) => `${t(`locales.${a.locale}`)}: ${a.value === null ? t("calibration.notMeasured") : percent(a.value)}`)
                            .join(t("listSeparator"))
                        : t("none")}
                    </td>
                    <td>
                      {r.running
                        ? t("calibration.running")
                        : r.latest?.finishedAt
                          ? `${format.dateTime(new Date(r.latest.finishedAt), { dateStyle: "medium" })} (${r.latest.outcome ? t(`calibration.outcomes.${r.latest.outcome}`) : t("none")})`
                          : t("none")}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
        {chosen ? (
          <section className="agents-section" aria-labelledby="eval-rubric-set">
            <h2 id="eval-rubric-set">{t("calibration.set", { rubric: label(chosen.metric) })}</h2>
            <p>{t("calibration.setHint")}</p>
            {chosen.canCalibrate ? <CalibrationStart metric={chosen.metric} running={chosen.running} /> : null}
            {labels && labels.ok ? <CalibrationLabels metric={chosen.metric} labels={labels.data} canLabel={chosen.canLabel} /> : <p>{t("calibration.labelsHidden")}</p>}
          </section>
        ) : null}
      </div>
    </>
  );
}
