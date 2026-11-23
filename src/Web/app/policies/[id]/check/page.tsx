import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { checkHref, nameIn, policyHref, type CheckState, type PolicyView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { CheckRunner } from "@/features/policies/CheckRunner";
import "@/features/policies/policies.css";

/**
 * The comprehension check a campaign asks before the attestation (spec 025 US6). Only the person who owes it finds
 * it; the API draws the questions and scores the answers.
 */
export default async function PolicyCheckPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [policy, check] = await Promise.all([
    apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`),
    apiGet<CheckState>(`/api/v1/policies/${encodeURIComponent(id)}/check`),
  ]);
  if (!policy.ok || !check.ok) {
    notFound();
  }
  const t = await getTranslations("policies.check");
  const locale = await getLocale();
  const name = nameIn(policy.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(id) },
          { label: t("title"), href: checkHref(id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} description={t("description", { pass: check.data.due.passPercent })} icon="policy" nav={<PoliciesNav current={null} />} />
        <CheckRunner policyId={id} initial={check.data} />
      </main>
    </>
  );
}
