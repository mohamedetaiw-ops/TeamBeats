import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { InboxItem } from "@/lib/services/types";
import { nameIn, policyHref, reviewHref, shownLocale, type CommentView, type CompareView, type PolicyView, type VersionView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { ReviewPanel } from "@/features/policies/ReviewPanel";

/**
 * The review page an approval task opens (spec 025 US1): the version in review compared with the one in force, clause
 * comments, and the decision for the person holding the task. Others who may not review find nothing here.
 */
export default async function ReviewPolicyPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const policy = await apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`);
  if (!policy.ok || !policy.data.working) {
    notFound();
  }
  const p = policy.data;
  const [draft, compare, tasks] = await Promise.all([
    apiGet<VersionView>(`/api/v1/policies/${encodeURIComponent(id)}/draft`),
    apiGet<CompareView>(`/api/v1/policies/${encodeURIComponent(id)}/compare`),
    apiGet<InboxItem[]>("/api/v1/tasks"),
  ]);
  if (!draft.ok || !compare.ok) {
    notFound();
  }
  const comments = await apiGet<CommentView[]>(`/api/v1/policies/${encodeURIComponent(id)}/drafts/${encodeURIComponent(draft.data.id)}/comments`);
  const task = tasks.ok ? tasks.data.find((i) => i.source === "policy_approval" && i.link === reviewHref(p.id)) : undefined;
  const t = await getTranslations("policies.review");
  const locale = await getLocale();
  const name = nameIn(p.names, locale);
  const shown = shownLocale(draft.data, locale, p.bindingLocale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(p.id) },
          { label: t("pageTitle"), href: reviewHref(p.id) },
        ]}
      />
      <main id="main" className="page policies-page" lang={shown}>
        <h1>{t("heading", { name: draft.data.titles[shown] ?? name })}</h1>
        <PoliciesNav current={null} />
        <ReviewPanel
          policyId={p.id}
          compare={compare.data}
          initialComments={comments.ok ? comments.data : []}
          locale={shown}
          taskId={task?.taskId ?? null}
          canResolve={p.rights.edit || p.rights.manage}
        />
      </main>
    </>
  );
}
