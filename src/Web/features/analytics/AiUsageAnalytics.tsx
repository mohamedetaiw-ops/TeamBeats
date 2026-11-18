"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { otherGroup, type AiUsageView } from "@/lib/analytics/types";
import { ChartCard, LineChart, seriesTable } from "@/components/ui/Charts";
import { StateView } from "@/components/ui/StateView";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/**
 * AI usage (spec 021 section 2, FR-016): requests, people, tokens and cost as the AI usage page counts them, assistant
 * answer, citation and helpful rates, and the agents in use with their evaluation scores. Figures behind fewer people
 * than the minimum group size are hidden.
 */
export function AiUsageDashboard({ view }: { view: AiUsageView }) {
  const t = useTranslations("analytics.aiUsage");
  const locale = useLocale();
  const format = useFormatter();
  const hidden = t("hidden", { size: view.minGroupSize });
  const number = (n: number | null) => (n === null ? hidden : format.number(n));
  // A rate is hidden with its group, and empty when there was nothing to count (no questions or no ratings).
  const share = (n: number | null, shown: boolean) => (!shown ? hidden : n === null ? t("noValue") : format.number(n, { style: "percent", maximumFractionDigits: 1 }));
  const money = format.number(view.cost * view.rate, { style: "currency", currency: view.currency, maximumFractionDigits: 2 });
  const week = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const label = (key: string, group: string) => (group === otherGroup ? t("other", { size: view.minGroupSize }) : t.has(`${key}.${group}`) ? t(`${key}.${group}`) : group);

  if (view.requests === 0 && view.denied.length === 0 && view.agents.length === 0) {
    return <StateView kind="empty" title={t("empty")} message={t("emptyHint")} />;
  }
  const shownWeeks = view.weeks.filter((w) => w.requests !== null);
  const series = [{ key: "requests", label: t("requests"), values: shownWeeks.map((w) => w.requests ?? 0) }];
  const whole = (n: number) => format.number(n);
  const a = view.assistant;

  return (
    <div className="analytics-item" data-testid="ai-usage-dashboard">
      <dl className="analytics-stats" data-testid="ai-usage-total">
        <div>
          <dt>{t("requests")}</dt>
          <dd>{format.number(view.requests)}</dd>
        </div>
        <div>
          <dt>{t("users")}</dt>
          <dd>{format.number(view.users)}</dd>
        </div>
        <div>
          <dt>{t("tokens")}</dt>
          <dd>{format.number(view.inputTokens + view.outputTokens)}</dd>
        </div>
        <div>
          <dt>{t("cost")}</dt>
          <dd>{money}</dd>
        </div>
      </dl>
      <p className="analytics-note">{t("sharedNote")}</p>
      {shownWeeks.length > 1 ? (
        <ChartCard title={t("trend")} table={seriesTable(t("column.week"), shownWeeks.map((w) => week(w.group)), series, whole)}>
          <LineChart label={t("trendSummary", { weeks: shownWeeks.length })} categories={shownWeeks.map((w) => week(w.group))} series={series} format={whole} />
        </ChartCard>
      ) : null}

      <section className="analytics-card" aria-labelledby="ai-usage-assistant">
        <h2 id="ai-usage-assistant">{t("assistant")}</h2>
        <dl className="analytics-stats" data-testid="ai-usage-assistant">
          <div>
            <dt>{t("questions")}</dt>
            <dd>{number(a.questions)}</dd>
          </div>
          <div>
            <dt>{t("answerRate")}</dt>
            <dd>{share(a.answerRate, a.questions !== null)}</dd>
          </div>
          <div>
            <dt>{t("citationRate")}</dt>
            <dd>{share(a.citationRate, a.questions !== null)}</dd>
          </div>
          <div>
            <dt>{t("helpfulRate")}</dt>
            <dd>{share(a.helpfulRate, a.questions !== null)}</dd>
          </div>
        </dl>
      </section>

      <section className="analytics-card" aria-labelledby="ai-usage-cases">
        <h2 id="ai-usage-cases">{t("byUseCase")}</h2>
        <div className="analytics-table">
          <table data-testid="ai-usage-cases">
            <thead>
              <tr>
                <th scope="col">{t("column.useCase")}</th>
                <th scope="col">{t("requests")}</th>
                <th scope="col">{t("users")}</th>
              </tr>
            </thead>
            <tbody>
              {view.useCases.map((u) => (
                <tr key={u.group}>
                  <th scope="row">{label("useCase", u.group)}</th>
                  <td data-number>{number(u.requests)}</td>
                  <td data-number>{u.group === otherGroup && u.requests !== null ? t("notCounted") : number(u.users)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {view.agents.length > 0 ? (
        <section className="analytics-card" aria-labelledby="ai-usage-agents">
          <h2 id="ai-usage-agents">{t("agents")}</h2>
          <div className="analytics-table">
            <table data-testid="ai-usage-agents">
              <thead>
                <tr>
                  <th scope="col">{t("column.agent")}</th>
                  <th scope="col">{t("runs")}</th>
                  <th scope="col">{t("successRate")}</th>
                  <th scope="col">{t("helpfulRate")}</th>
                  <th scope="col">{t("quality")}</th>
                </tr>
              </thead>
              <tbody>
                {view.agents.map((g) => (
                  <tr key={g.agentId}>
                    <th scope="row">
                      <bdi>{pick(g.name, locale) || t("unnamed")}</bdi>
                    </th>
                    <td data-number>{number(g.runs)}</td>
                    <td data-number>{share(g.successRate, g.runs !== null)}</td>
                    <td data-number>{share(g.helpfulRate, g.runs !== null)}</td>
                    <td>
                      {g.gate ? (t.has(`gate.${g.gate}`) ? t(`gate.${g.gate}`) : g.gate) : t("noGate")}
                      {g.quality.length > 0 ? (
                        <ul className="analytics-summary-figures">
                          {g.quality.map((q) => (
                            <li key={`${q.metric}-${q.locale}`}>
                              <bdi dir="ltr">{t("metric", { metric: q.metric, locale: q.locale })}</bdi> {q.value === null ? t("noScore") : format.number(q.value, { maximumFractionDigits: 2 })}
                            </li>
                          ))}
                        </ul>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}

      {view.denied.length > 0 ? (
        <section className="analytics-card" aria-labelledby="ai-usage-denied">
          <h2 id="ai-usage-denied">{t("refused")}</h2>
          <div className="analytics-table">
            <table data-testid="ai-usage-denied">
              <thead>
                <tr>
                  <th scope="col">{t("column.reason")}</th>
                  <th scope="col">{t("requests")}</th>
                </tr>
              </thead>
              <tbody>
                {view.denied.map((d) => (
                  <tr key={d.reason}>
                    <th scope="row">{label("reason", d.reason)}</th>
                    <td data-number>{format.number(d.requests)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}
      <p className="analytics-note">{t("privacyNote", { size: view.minGroupSize })}</p>
    </div>
  );
}
