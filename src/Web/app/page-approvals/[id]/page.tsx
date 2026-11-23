import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getApproval } from "@/lib/experience/client";
import { pagePath, type PageSubmission } from "@/lib/experience/builder-types";
import { Header } from "@/components/shell/Header";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { PageApprovalPanel } from "@/features/builder/PageApprovalPanel";
import "@/features/builder/builder.css";

const tones: Record<PageSubmission["status"], BadgeTone> = { pending: "warning", approved: "success", returned: "danger", withdrawn: "neutral" };

/**
 * A page submission for its approver (spec 032 R2 T023, US6 scenario 1): what changed against the release in force,
 * placement by placement, the page as it would go live, and approve or return from the inbox task. The editor sees the
 * same page to follow their submission. It sits outside the admin area, as a named approver may hold no admin
 * rights; the API decides who may see and decide it.
 */
export default async function PageApprovalPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const approval = await getApproval(id);
  const t = await getTranslations("builder.approval");
  const format = await getFormatter();
  if (!approval.ok) {
    if (approval.status === 403) {
      return (
        <>
          <Header me={me.data} />
          <main id="main" className="page">
            <PageHeader title={t("title")} icon="edit" />
            <StateView kind="empty" title={t("offTitle")} message={t("off")} />
          </main>
        </>
      );
    }
    notFound();
  }
  const shown = approval.data.pending ?? approval.data.last;
  const when = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });
  return (
    <>
      <Header me={me.data} />
      <main id="main" className="page">
        <PageHeader
          title={shown ? t("heading", { page: pagePath(shown.slug) }) : t("title")}
          description={approval.data.required ? t("required") : t("notRequired")}
          icon="edit"
          actions={
            shown && !shown.decide ? (
              <Link className="button button-secondary" href={`/admin/pages/${encodeURIComponent(id)}`}>
                {t("back")}
              </Link>
            ) : null
          }
        />
        {!shown ? (
          <StateView kind="empty" title={t("noneTitle")} message={t("none")} />
        ) : (
          <>
            <dl className="builder-facts" data-testid="approval-facts">
              <dt>{t("status")}</dt>
              <dd>
                <Badge tone={tones[shown.status]}>{t(`statuses.${shown.status}`)}</Badge>
              </dd>
              <dt>{t("submittedAt")}</dt>
              <dd>{when(shown.submittedAt)}</dd>
              {shown.reason ? (
                <>
                  <dt>{t("reason")}</dt>
                  <dd>
                    <bdi>{shown.reason}</bdi>
                  </dd>
                </>
              ) : null}
              {shown.comment ? (
                <>
                  <dt>{t("comment")}</dt>
                  <dd>
                    <bdi>{shown.comment}</bdi>
                  </dd>
                </>
              ) : null}
              {shown.release !== null ? (
                <>
                  <dt>{t("release")}</dt>
                  <dd>{shown.release}</dd>
                </>
              ) : null}
            </dl>
            <section className="builder-region" aria-labelledby="approval-changes">
              <h2 id="approval-changes">{t("changes")}</h2>
              <p className="builder-template-description">
                {shown.diff.baseRelease === null ? t("firstRelease") : t("against", { release: shown.diff.baseRelease })}
                {shown.diff.fromTemplate && shown.diff.fromTemplate !== shown.diff.toTemplate ? <> {t("templateChanged")}</> : null}
              </p>
              {shown.diff.changes.length === 0 ? (
                <p>{t("noChanges")}</p>
              ) : (
                <ul className="builder-change-list" data-testid="approval-changes">
                  {shown.diff.changes.map((c) => (
                    <li key={`${c.placementId}-${c.change}`}>
                      <Badge tone={c.change === "removed" ? "danger" : c.change === "added" ? "success" : "info"}>{t(`change.${c.change}`)}</Badge>{" "}
                      <bdi dir="ltr">{c.componentRef}</bdi>{" "}
                      {t("where", { region: c.toRegion ?? c.fromRegion ?? "" })}
                    </li>
                  ))}
                </ul>
              )}
            </section>
            {shown.status === "pending" ? (
              <section className="builder-region builder-panel" aria-labelledby="approval-preview">
                <h2 id="approval-preview">{t("preview")}</h2>
                <div className="builder-preview-frame" data-device="desktop" tabIndex={0} role="region" aria-label={t("previewFrame")}>
                  <iframe title={t("previewFrame")} src={`/preview/pages/${encodeURIComponent(id)}?approval=1`} data-testid="approval-preview-frame" />
                </div>
              </section>
            ) : null}
            {shown.status === "pending" && shown.decide && task ? <PageApprovalPanel taskId={task} /> : null}
          </>
        )}
      </main>
    </>
  );
}
