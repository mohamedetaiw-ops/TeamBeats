import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { editHref, nameIn, policyHref, reviewsHref, type ReviewsView } from "@/lib/policies/types";
import { Alert } from "@/components/ui/Alert";
import { ButtonLink } from "@/components/ui/Button";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { Badge } from "@/components/ui/Badge";
import { FactList } from "@/components/ui/Patterns";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { ReviewOutcomeForm } from "@/features/policies/ReviewOutcomeForm";
import "@/features/policies/policies.css";

/**
 * Scheduled reviews of a policy (spec 025 US5): the cycle and window, the open review with its four outcomes, and the
 * review history with the reviewer and date. For the owner and compliance officers; the API refuses anyone else.
 */
export default async function PolicyReviewsPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ recorded?: string }> }) {
  const { id } = await params;
  const { recorded } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const reviews = await apiGet<ReviewsView>(`/api/v1/policies/${encodeURIComponent(id)}/reviews`);
  if (!reviews.ok) {
    notFound();
  }
  const v = reviews.data;
  const t = await getTranslations("policies.reviews");
  const locale = await getLocale();
  const format = await getFormatter();
  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium" }) : t("none"));
  const name = nameIn(v.policy.names, locale);
  // After an outcome is recorded the form sends the person here with it, so the page can say what happened.
  const last = v.history[0];
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(v.policy.id) },
          { label: t("title"), href: reviewsHref(v.policy.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} description={t("description")} icon="policy" nav={<PoliciesNav current={null} />} />
        {last && recorded === last.outcome ? (
          <div data-testid="review-done">
            <Alert
              tone="success"
              live
              title={t(`done.${last.outcome}`, { date: date(last.nextReviewAt) })}
              action={
                last.draftVersionId && (last.outcome === "minor_update" || last.outcome === "major_revision") && v.policy.status !== "retired" ? (
                  <ButtonLink href={editHref(v.policy.id)} variant="primary" icon="edit">
                    {t("openDraft")}
                  </ButtonLink>
                ) : null
              }
            />
          </div>
        ) : null}
        <section className="policies-card" aria-labelledby="review-cycle" data-testid="review-cycle">
          <h2 id="review-cycle">{t("cycle")}</h2>
          <FactList
            facts={[
              { key: "months", label: t("every"), value: t("months", { count: v.reviewMonths }) },
              { key: "window", label: t("window"), value: t("days", { count: v.windowDays }) },
              {
                key: "next",
                label: t("next"),
                value: (
                  <>
                    {date(v.nextReviewAt)}
                    {v.state ? (
                      <>
                        {" "}
                        <Badge tone={v.state === "overdue" ? "warning" : "info"}>{t(`state.${v.state}`)}</Badge>
                      </>
                    ) : null}
                  </>
                ),
              },
            ]}
          />
        </section>
        {v.open ? (
          <ReviewOutcomeForm policyId={v.policy.id} review={v.open} rights={v.rights} />
        ) : (
          <p className="policies-meta" data-testid="review-none">
            {t("noOpen", { date: date(v.nextReviewAt) })}
          </p>
        )}
        <section className="policies-card" aria-labelledby="review-history">
          <h2 id="review-history">{t("history")}</h2>
          {v.history.length === 0 ? (
            <StateView kind="empty" title={t("historyEmpty")} message="" />
          ) : (
            <div className="policies-table">
              <table data-testid="review-history" data-stack>
                <thead>
                  <tr>
                    <th scope="col">{t("due")}</th>
                    <th scope="col">{t("outcome")}</th>
                    <th scope="col">{t("reviewer")}</th>
                    <th scope="col">{t("completed")}</th>
                    <th scope="col">{t("nextAfter")}</th>
                  </tr>
                </thead>
                <tbody>
                  {v.history.map((r) => (
                    <tr key={r.id}>
                      <th scope="row" data-label={t("due")}>
                        {date(r.dueAt)}
                      </th>
                      <td data-label={t("outcome")}>
                        {r.outcome ? t(`outcomes.${r.outcome}`) : t("none")}
                        {r.note ? (
                          <>
                            {" · "}
                            <bdi>{r.note}</bdi>
                          </>
                        ) : null}
                      </td>
                      <td data-label={t("reviewer")}>{r.reviewer ? <bdi>{r.reviewer.displayName}</bdi> : t("system")}</td>
                      <td data-label={t("completed")}>{date(r.completedAt)}</td>
                      <td data-label={t("nextAfter")}>{date(r.nextReviewAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </main>
    </>
  );
}
