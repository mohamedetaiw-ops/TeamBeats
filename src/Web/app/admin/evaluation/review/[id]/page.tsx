import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { ReviewItemView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { ReviewDecision } from "@/features/evaluation/ReviewDecision";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * One flagged production sample (spec 034 US8 scenarios 2 and 3): why it was flagged, its scores, the redacted question,
 * answer, evidence and steps while the content is kept and the reviewer may see it, and the reviewer's decision.
 */
export default async function EvaluationReviewItemPage({ params }: { params: Promise<{ id: string }> }) {
  await evaluationSection();
  const { id } = await params;
  const item = await apiGet<ReviewItemView>(`/api/v1/evaluation/review/${encodeURIComponent(id)}`);
  if (!item.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  const i = item.data;
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const percent = (v: number | null) => (v === null ? t("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  const scores = Object.entries(i.metrics);
  return (
    <>
      <AdminHeader section="evaluation" title={pickText(i.agentName, locale) || t("untitled")} nav={<EvaluationTabs current="review" />} />
      <div className="agents">
        <p>
          <Link href={`/admin/evaluation/review?status=${i.status}`}>{t("review.back")}</Link>
        </p>
        <section className="agents-section" aria-labelledby="eval-review-item">
          <h2 id="eval-review-item">{t("review.item")}</h2>
          <p className="agents-row">
            <span className="agents-badge">{t(`review.statuses.${i.status}`)}</span>
            <span>{t("dashboard.versionNumber", { number: String(i.agentVersion) })}</span>
            <span>{t.has(`locales.${i.language}`) ? t(`locales.${i.language}`) : i.language}</span>
            <span>{t.has(`classification.${i.classification}`) ? t(`classification.${i.classification}`) : i.classification}</span>
            <span>{format.dateTime(new Date(i.createdAt), { dateStyle: "medium", timeStyle: "short" })}</span>
          </p>
          <p>
            {t("review.reasons")}: {i.reasons.map((r) => (t.has(`review.reason.${r}`) ? t(`review.reason.${r}`) : r)).join(t("listSeparator"))}
          </p>
          {scores.length > 0 ? (
            <div className="agents-table">
              <table data-testid="eval-review-scores">
                <caption>{t("review.scores")}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t("metric")}</th>
                    <th scope="col">{t("productionView.value")}</th>
                  </tr>
                </thead>
                <tbody>
                  {scores.map(([key, m]) => (
                    <tr key={key}>
                      <th scope="row">{label(key)}</th>
                      <td data-number>{m.state === "scored" ? percent(m.value) : t(`caseMetricState.${m.state}`)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
        </section>
        <section className="agents-section" aria-labelledby="eval-review-content">
          <h2 id="eval-review-content">{t("review.content")}</h2>
          {i.content ? (
            <div data-testid="eval-review-content">
              <h3>{t("review.question")}</h3>
              <p dir="auto">{i.content.question}</p>
              <h3>{t("review.answer")}</h3>
              <p dir="auto">{i.content.answer ?? t("none")}</p>
              {i.content.evidence ? (
                <>
                  <h3>{t("review.evidence")}</h3>
                  <p dir="auto">{i.content.evidence}</p>
                </>
              ) : null}
              {i.content.steps.length > 0 ? (
                <>
                  <h3>{t("review.steps")}</h3>
                  <ol>
                    {i.content.steps.map((s) => (
                      <li key={s.n}>
                        <bdi dir="ltr">{s.tool ?? s.kind}</bdi> {s.decision}
                        {s.reason ? (
                          <>
                            {" "}
                            <span dir="auto">{s.reason}</span>
                          </>
                        ) : null}
                      </li>
                    ))}
                  </ol>
                </>
              ) : null}
            </div>
          ) : (
            <p role="status">{t(`review.withheld.${i.contentWithheld ?? "not_retained"}`)}</p>
          )}
        </section>
      </div>
      <ReviewDecision initial={i} locale={locale} />
    </>
  );
}
