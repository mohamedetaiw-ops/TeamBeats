import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, policyHref, questionsHref, type CheckQuestions, type PolicyReading, type PolicyView, type VersionView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { QuestionsEditor } from "@/features/policies/QuestionsEditor";
import "@/features/policies/policies.css";

/**
 * The comprehension question bank of a policy (spec 025 US6, FR-013): for the working version when there is one,
 * else the version in force. Each question is linked to a clause. For the owner and compliance officers in a package
 * with comprehension checks; the API refuses anyone else.
 */
export default async function PolicyQuestionsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const policy = await apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`);
  if (!policy.ok || !policy.data.rights.comprehension) {
    notFound();
  }
  const p = policy.data;
  const version: VersionView | null = p.working
    ? await apiGet<VersionView>(`/api/v1/policies/${encodeURIComponent(id)}/draft`).then((r) => (r.ok ? r.data : null))
    : p.currentVersion
      ? await apiGet<PolicyReading>(`/api/v1/policies/${encodeURIComponent(id)}/current`).then((r) => (r.ok ? r.data.version : null))
      : null;
  const t = await getTranslations("policies.questions");
  const locale = await getLocale();
  const name = nameIn(p.names, locale);
  const bank = version ? await apiGet<CheckQuestions>(`/api/v1/policies/${encodeURIComponent(id)}/drafts/${encodeURIComponent(version.id)}/questions`) : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(id) },
          { label: t("title"), href: questionsHref(id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} description={t("description")} icon="policy" nav={<PoliciesNav current={null} />} />
        {version && bank?.ok ? (
          <QuestionsEditor policyId={id} version={version} initial={bank.data} />
        ) : (
          <StateView kind="empty" title={t("noVersion")} message="" />
        )}
      </main>
    </>
  );
}
