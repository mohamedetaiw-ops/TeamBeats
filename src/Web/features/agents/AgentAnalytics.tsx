import { getFormatter, getTranslations } from "next-intl/server";
import type { AgentAnalyticsView, AgentVersionStats } from "@/lib/agents/types";

/**
 * Basic agent analytics (spec 020 FR-018, R1 part): runs, active users, task success, tool calls and denials, ratings,
 * tokens, estimated cost and the release gate result per version over the last days. The API decides who may see it
 * and computes every number; this only lays them out.
 */
export async function AgentAnalytics({ view }: { view: AgentAnalyticsView }) {
  const t = await getTranslations("agents");
  const format = await getFormatter();
  const percent = (rate: number | null) => (rate === null ? t("analytics.none") : format.number(rate, { style: "percent", maximumFractionDigits: 0 }));
  const row = (s: AgentVersionStats, label: string) => (
    <tr key={label}>
      <th scope="row">{label}</th>
      <td data-number>{format.number(s.runs)}</td>
      <td data-number>{format.number(s.users)}</td>
      <td data-number>{percent(s.successRate)}</td>
      <td data-number>{format.number(s.stopped + s.failed)}</td>
      <td data-number>{format.number(s.toolCalls)}</td>
      <td data-number>{format.number(s.toolDenials)}</td>
      <td data-number>{s.ratings === 0 ? t("analytics.none") : t("analytics.helpfulOf", { helpful: format.number(s.helpful), ratings: format.number(s.ratings) })}</td>
      <td data-number>{format.number(s.tokens)}</td>
      <td data-number>{format.number(s.cost, { maximumFractionDigits: 2 })}</td>
      <td>{s.quality && t.has(`review.gateStatus.${s.quality}`) ? t(`review.gateStatus.${s.quality}`) : t("analytics.none")}</td>
    </tr>
  );
  return (
    <section className="agents-section" aria-labelledby="agent-analytics" data-testid="agent-analytics">
      <h2 id="agent-analytics">{t("analytics.title")}</h2>
      <p>{t("analytics.period", { days: format.number(view.days) })}</p>
      <div className="agents-table">
        <table>
          <thead>
            <tr>
              <th scope="col">{t("analytics.version")}</th>
              <th scope="col">{t("analytics.runs")}</th>
              <th scope="col">{t("analytics.users")}</th>
              <th scope="col">{t("analytics.success")}</th>
              <th scope="col">{t("analytics.unfinished")}</th>
              <th scope="col">{t("analytics.toolCalls")}</th>
              <th scope="col">{t("analytics.toolDenials")}</th>
              <th scope="col">{t("analytics.ratings")}</th>
              <th scope="col">{t("analytics.tokens")}</th>
              <th scope="col">{t("analytics.cost")}</th>
              <th scope="col">{t("analytics.quality")}</th>
            </tr>
          </thead>
          <tbody>
            {view.versions.map((v) => row(v, t("analytics.versionNumber", { number: String(v.version) })))}
            {row(view.total, t("analytics.total"))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
