import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { editHref, nameIn, policyHref, type PolicyReading, type PolicyView, type VersionView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { DraftEditor } from "@/features/policies/DraftEditor";
import { PageHeader } from "@/components/ui/PageHeader";

/**
 * Writing a version (spec 025 US1): the working draft, or for a revision a copy of the version in force with its clause
 * keys, so compare and anchors follow each clause. Only the owner and compliance officers reach it.
 */
export default async function EditPolicyPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const policy = await apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`);
  if (!policy.ok || !(policy.data.rights.edit || policy.data.rights.manage)) {
    notFound();
  }
  const p = policy.data;
  let initial: VersionView | null = null;
  if (p.working) {
    const draft = await apiGet<VersionView>(`/api/v1/policies/${encodeURIComponent(id)}/draft`);
    initial = draft.ok ? draft.data : null;
  } else if (p.currentVersion) {
    const current = await apiGet<PolicyReading>(`/api/v1/policies/${encodeURIComponent(id)}/current`);
    initial = current.ok ? { ...current.data.version, state: "draft", changeNote: null } : null;
  }
  const t = await getTranslations("policies.editor");
  const locale = await getLocale();
  const name = nameIn(p.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(p.id) },
          { label: t("pageTitle"), href: editHref(p.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} icon="policy" nav={<PoliciesNav current={null} />} />
        <DraftEditor policyId={p.id} initial={initial} locales={me.data.tenant.enabledLocales} />
      </main>
    </>
  );
}
