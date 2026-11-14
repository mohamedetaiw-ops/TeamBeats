import { getFormatter, getTranslations } from "next-intl/server";
import type { AgentAnalyticsDetail, AgentAnalyticsView, AgentVersionStats } from "@/lib/agents/types";

/**
 * Agent analytics (spec 020 FR-018): runs, active users, task success, tool calls and denials, ratings, tokens,
 * estimated cost and the release gate result per version over the last days. When the package has detailed analytics
 * (R2), the API adds conversations, escalations, evaluation scores, how runs ended, tool success and failure and a day by
 * day trend. The API decides who may see it and computes every number; this only lays them out.
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
      {view.detail ? <AgentAnalyticsMore detail={view.detail} /> : null}
    </section>
  );
}

async function AgentAnalyticsMore({ detail }: { detail: AgentAnalyticsDetail }) {
  const t = await getTranslations("agents");
  const te = await getTranslations("evaluation");
  const format = await getFormatter();
  const percent = (rate: number | null) => (rate === null ? t("analytics.none") : format.number(rate, { style: "percent", maximumFractionDigits: 0 }));
  const label = (group: string, key: string) => (t.has(`${group}.${key}`) ? t(`${group}.${key}`) : key);
  const reason = (key: string) => label("analytics.reasons", key.replace(/^tool\./, ""));
  const metric = (key: string) => (te.has(`metrics.${key}`) ? te(`metrics.${key}`) : key);
  return (
    <div data-testid="agent-analytics-detail">
      <h3>{t("analytics.detailTitle")}</h3>
      <div className="agents-table">
        <table>
          <thead>
            <tr>
              <th scope="col">{t("analytics.version")}</th>
              <th scope="col">{t("analytics.conversations")}</th>
              <th scope="col">{t("analytics.escalations")}</th>
              <th scope="col">{t("analytics.resolved")}</th>
              <th scope="col">{t("analytics.scores")}</th>
            </tr>
          </thead>
          <tbody>
            {detail.versions.map((v) => (
              <tr key={v.version ?? "total"}>
                <th scope="row">{v.version === null ? t("analytics.total") : t("analytics.versionNumber", { number: String(v.version) })}</th>
                <td data-number>{format.number(v.conversations)}</td>
                <td data-number>{format.number(v.escalations)}</td>
                <td data-number>{percent(v.resolvedRate)}</td>
                <td>
                  {v.quality.length === 0
                    ? t("analytics.none")
                    : v.quality
                        .map((q) => t("analytics.score", { metric: metric(q.metric), locale: q.locale, value: q.value === null ? t("analytics.none") : format.number(q.value, { maximumFractionDigits: 2 }) }))
                        .join(", ")}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h3>{t("analytics.outcomesTitle")}</h3>
      {detail.outcomes.length === 0 ? (
        <p>{t("analytics.noRuns")}</p>
      ) : (
        <div className="agents-table">
          <table>
            <thead>
              <tr>
                <th scope="col">{t("analytics.outcome")}</th>
                <th scope="col">{t("analytics.runs")}</th>
              </tr>
            </thead>
            <tbody>
              {detail.outcomes.map((o) => (
                <tr key={o.outcome}>
                  <th scope="row">{label("outcomes", o.outcome)}</th>
                  <td data-number>{format.number(o.runs)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h3>{t("analytics.toolsTitle")}</h3>
      {detail.tools.length === 0 ? (
        <p>{t("analytics.noTools")}</p>
      ) : (
        <div className="agents-table">
          <table>
            <thead>
              <tr>
                <th scope="col">{t("analytics.tool")}</th>
                <th scope="col">{t("analytics.toolCalls")}</th>
                <th scope="col">{t("analytics.succeeded")}</th>
                <th scope="col">{t("analytics.toolFailed")}</th>
                <th scope="col">{t("analytics.toolSuccess")}</th>
                <th scope="col">{t("analytics.averageTime")}</th>
                <th scope="col">{t("analytics.failures")}</th>
              </tr>
            </thead>
            <tbody>
              {detail.tools.map((tool) => (
                <tr key={tool.tool}>
                  <th scope="row">{label("toolNames", tool.tool)}</th>
                  <td data-number>{format.number(tool.calls)}</td>
                  <td data-number>{format.number(tool.succeeded)}</td>
                  <td data-number>{format.number(tool.failed)}</td>
                  <td data-number>{percent(tool.successRate)}</td>
                  <td data-number>{t("analytics.milliseconds", { ms: format.number(tool.averageMs) })}</td>
                  <td>
                    {tool.failures.length === 0
                      ? t("analytics.none")
                      : tool.failures.map((f) => t("analytics.failureCount", { reason: reason(f.reason), calls: format.number(f.calls) })).join(", ")}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h3>{t("analytics.trendTitle")}</h3>
      {detail.trend.length === 0 ? (
        <p>{t("analytics.noRuns")}</p>
      ) : (
        <div className="agents-table">
          <table>
            <thead>
              <tr>
                <th scope="col">{t("analytics.day")}</th>
                <th scope="col">{t("analytics.runs")}</th>
                <th scope="col">{t("analytics.answered")}</th>
                <th scope="col">{t("analytics.escalations")}</th>
                <th scope="col">{t("analytics.ratings")}</th>
              </tr>
            </thead>
            <tbody>
              {detail.trend.map((d) => (
                <tr key={d.day}>
                  <th scope="row">{format.dateTime(new Date(`${d.day}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" })}</th>
                  <td data-number>{format.number(d.runs)}</td>
                  <td data-number>{format.number(d.answered)}</td>
                  <td data-number>{format.number(d.escalations)}</td>
                  <td data-number>{d.ratings === 0 ? t("analytics.none") : t("analytics.helpfulOf", { helpful: format.number(d.helpful), ratings: format.number(d.ratings) })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
