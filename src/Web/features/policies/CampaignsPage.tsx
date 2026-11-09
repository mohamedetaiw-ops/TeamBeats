import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { campaignsHref, nameIn, policyHref, type CampaignView, type PolicyView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PoliciesNav } from "./PoliciesNav";
import { CampaignsPanel } from "./CampaignsPanel";

/** The campaigns page of a policy, for its owner and compliance officers (spec 025 US2); others find nothing. */
export async function CampaignsPage({ id, focus }: { id: string; focus: string | null }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const policy = await apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`);
  if (!policy.ok || !policy.data.rights.history) {
    notFound();
  }
  const campaigns = await apiGet<CampaignView[]>(`/api/v1/policies/${encodeURIComponent(id)}/campaigns`);
  if (!campaigns.ok) {
    notFound();
  }
  const p = policy.data;
  const t = await getTranslations("policies.campaigns");
  const locale = await getLocale();
  const name = nameIn(p.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(p.id) },
          { label: t("title"), href: campaignsHref(p.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <h1>{t("heading", { name })}</h1>
        <PoliciesNav current={null} />
        <CampaignsPanel policyId={p.id} initial={campaigns.data} canLaunch={p.currentVersion !== null && p.status !== "retired"} focus={focus} />
      </main>
    </>
  );
}
