import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, paperHref, policyHref, type PaperImportView, type PolicyView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { PaperImport } from "@/features/policies/PaperImport";
import "@/features/policies/policies.css";

/**
 * Paper attestations of a policy (spec 025 section 14): compliance officers attach the scanned signed forms, import a
 * CSV of who signed which version on which day, and see each import's report. The API refuses anyone else.
 */
export default async function PaperAttestationsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [policy, imports] = await Promise.all([
    apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`),
    apiGet<PaperImportView[]>(`/api/v1/policies/${encodeURIComponent(id)}/paper-imports`),
  ]);
  if (!policy.ok || !imports.ok) {
    notFound();
  }
  const t = await getTranslations("policies.paper");
  const locale = await getLocale();
  const name = nameIn(policy.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(policy.data.id) },
          { label: t("title"), href: paperHref(policy.data.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} description={t("description")} icon="policy" nav={<PoliciesNav current={null} />} />
        <PaperImport policyId={policy.data.id} imports={imports.data} canImport={policy.data.rights.paper === true} />
      </main>
    </>
  );
}
