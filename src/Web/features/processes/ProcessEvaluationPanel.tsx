import { getFormatter, getTranslations } from "next-intl/server";
import { PROCESS_STEP_CHECKS, type ProcessEvalView } from "@/lib/evaluation/types";

/**
 * The end to end evaluation of a process's L4 agent tasks (spec 034 R3): per version, the instances and how many reached
 * an end, the steps that passed every check and those that failed as critical, and the share of steps passing each
 * trajectory check, as tables. The API shows only steps of agents in units where the person holds an evaluation permission.
 */
export async function ProcessEvaluationPanel({ view }: { view: ProcessEvalView }) {
  const t = await getTranslations("processes.evaluation");
  const format = await getFormatter();
  const percent = (v: number | null | undefined) => (v === null || v === undefined ? t("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  return (
    <section className="agents-section" aria-labelledby="process-evaluation" data-testid="process-evaluation">
      <h2 id="process-evaluation">{t("title")}</h2>
      <p>{t("intro")}</p>
      <div className="agents-table">
        <table>
          <caption>{t("versionsCaption")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("version")}</th>
              <th scope="col">{t("instances")}</th>
              <th scope="col">{t("steps")}</th>
              <th scope="col">{t("passed")}</th>
              <th scope="col">{t("critical")}</th>
              {PROCESS_STEP_CHECKS.map((c) => (
                <th scope="col" key={c}>
                  {t(`checks.${c}`)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {view.versions.map((v) => (
              <tr key={v.version}>
                <th scope="row">{t("versionNumber", { number: String(v.version) })}</th>
                <td data-number>{t("instancesValue", { ended: String(v.completedInstances), closed: String(v.closed), total: String(v.instances) })}</td>
                <td data-number>{format.number(v.steps)}</td>
                <td data-number>{format.number(v.passedSteps)}</td>
                <td data-number>{format.number(v.criticalSteps)}</td>
                {PROCESS_STEP_CHECKS.map((c) => (
                  <td key={c} data-number>
                    {percent(v.rates[c])}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {view.recent.length > 0 ? (
        <div className="agents-table">
          <table data-testid="process-evaluation-steps">
            <caption>{t("recentCaption")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("recorded")}</th>
                <th scope="col">{t("element")}</th>
                <th scope="col">{t("result")}</th>
                <th scope="col">{t("failedChecks")}</th>
              </tr>
            </thead>
            <tbody>
              {view.recent.map((s) => {
                const failed = PROCESS_STEP_CHECKS.filter((c) => s.metrics[c] === false);
                return (
                  <tr key={s.serviceTaskId}>
                    <th scope="row">{format.dateTime(new Date(s.recordedAt), { dateStyle: "medium", timeStyle: "short" })}</th>
                    <td>
                      <bdi>{s.elementId}</bdi>
                    </td>
                    <td>
                      {t(s.critical ? "results.critical" : s.passed ? "results.passed" : "results.failed")}
                    </td>
                    <td>{failed.length === 0 ? t("none") : failed.map((c) => t(`checks.${c}`)).join(t("listSeparator"))}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : null}
    </section>
  );
}
