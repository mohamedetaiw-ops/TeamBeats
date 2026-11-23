import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { OrgUnitView } from "@/lib/admin/types";
import { exemptionsHref, nameIn, policyHref, type PolicyExemptionsView } from "@/lib/policies/types";
import { Alert } from "@/components/ui/Alert";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { ExemptionList } from "@/features/policies/ExemptionList";
import { ExemptionRequestForm } from "@/features/policies/ExemptionRequestForm";
import "@/features/policies/policies.css";

/**
 * Exemptions of one policy (spec 025 US7): the request form for the people it applies to (and, for compliance
 * officers, for someone else or a unit), and the exemptions the person may see: their own, or all of them for the
 * owner and compliance officers. The API decides who sees and asks what.
 */
export default async function PolicyExemptionsPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ asked?: string; exemption?: string }>;
}) {
  const { id } = await params;
  const { asked, exemption } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<PolicyExemptionsView>(`/api/v1/policies/${encodeURIComponent(id)}/exemptions`);
  if (!view.ok) {
    notFound();
  }
  const v = view.data;
  const t = await getTranslations("policies.exemptions");
  const locale = await getLocale();
  const name = nameIn(v.policy.names, locale);
  const units = v.rights.requestFor ? await apiGet<OrgUnitView[]>("/api/v1/org/units") : null;
  const unitOptions = units?.ok
    ? units.data.filter((u) => u.status === "active").map((u) => ({ value: u.id, label: `${" ".repeat(Math.max(0, u.level))}${nameIn(u.names, locale)}` }))
    : [];
  const justAsked = asked ? v.items.find((e) => e.id === asked) : undefined;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(v.policy.id) },
          { label: t("title"), href: exemptionsHref(v.policy.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} description={t("description")} icon="policy" nav={<PoliciesNav current={null} />} />
        {justAsked ? (
          <div data-testid="exemption-asked">
            <Alert tone="success" live title={t("asked")} />
          </div>
        ) : null}
        {v.rights.request || v.rights.requestFor ? <ExemptionRequestForm policyId={v.policy.id} requestFor={v.rights.requestFor} units={unitOptions} /> : null}
        <section className="policies-card" aria-labelledby="exemption-items">
          <h2 id="exemption-items">{t(v.rights.seesAll ? "all" : "mine")}</h2>
          {v.items.length === 0 ? <StateView kind="empty" title={t("none")} message="" /> : <ExemptionList items={v.items} focus={exemption ?? asked ?? null} />}
        </section>
      </main>
    </>
  );
}
