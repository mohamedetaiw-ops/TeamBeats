import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { ReviewQueueView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

const STATUSES = ["open", "reviewed"] as const;

/**
 * The human review queue (spec 034 US8 scenario 2): production samples flagged for a low score, negative feedback, an
 * uncertain judge or a policy denial, for evaluation reviewers of the agents' organization units. The API decides scope.
 */
export default async function EvaluationReviewPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  await evaluationSection();
  const { status: given } = await searchParams;
  const status = STATUSES.find((s) => s === given) ?? "open";
  const queue = await apiGet<ReviewQueueView>(`/api/v1/evaluation/review?status=${status}`);
  if (!queue.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  return (
    <>
      <AdminHeader section="evaluation" title={t("review.title")} nav={<EvaluationTabs current="review" />} />
      <div className="agents">
        <p>{t("review.intro")}</p>
        {!queue.data.available ? (
          <p role="status">{t("review.unavailable")}</p>
        ) : (
          <>
            <nav aria-label={t("review.filter")} className="agents-row">
              {STATUSES.map((s) => (
                <Link key={s} href={`/admin/evaluation/review?status=${s}`} aria-current={s === status ? "page" : undefined}>
                  {t(`review.statuses.${s}`)}
                </Link>
              ))}
            </nav>
            {queue.data.items.length === 0 ? <p data-testid="eval-review-empty">{t(`review.empty.${status}`)}</p> : null}
            {queue.data.items.length > 0 ? (
              <div className="agents-table">
                <table data-testid="eval-review-queue">
                  <thead>
                    <tr>
                      <th scope="col">{t("review.agent")}</th>
                      <th scope="col">{t("dashboard.version")}</th>
                      <th scope="col">{t("locale")}</th>
                      <th scope="col">{t("review.reasons")}</th>
                      <th scope="col">{status === "open" ? t("review.flagged") : t("review.reviewedAt")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {queue.data.items.map((i) => (
                      <tr key={i.id}>
                        <th scope="row">
                          <Link href={`/admin/evaluation/review/${i.id}`}>
                            <bdi>{pickText(i.agentName, locale) || t("untitled")}</bdi>
                          </Link>
                        </th>
                        <td>{t("dashboard.versionNumber", { number: String(i.agentVersion) })}</td>
                        <td>{t.has(`locales.${i.language}`) ? t(`locales.${i.language}`) : i.language}</td>
                        <td>{i.reasons.map((r) => (t.has(`review.reason.${r}`) ? t(`review.reason.${r}`) : r)).join(t("listSeparator"))}</td>
                        <td>{format.dateTime(new Date(status === "open" ? i.createdAt : (i.reviewedAt ?? i.createdAt)), { dateStyle: "medium", timeStyle: "short" })}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}
          </>
        )}
      </div>
    </>
  );
}
