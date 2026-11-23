import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { exemptionRegisterHref, type ExemptionStatus, type ExemptionView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { SectionNav } from "@/components/ui/SectionNav";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { ExemptionList } from "@/features/policies/ExemptionList";
import "@/features/policies/policies.css";

const filters = ["live", "pending", "approved", "rejected", "withdrawn", "expired"] as const;

/**
 * The exemption register (spec 025 US7 scenario 2): every exemption of the policies the compliance officer manages,
 * pending and approved by default, with scope, reason, approver and end date. The API refuses anyone else.
 */
export default async function ExemptionRegisterPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  const { status } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const chosen = filters.find((f) => f === status && f !== "live") as ExemptionStatus | undefined;
  const list = await apiGet<ExemptionView[]>(`/api/v1/policies/exemptions${chosen ? `?status=${chosen}` : ""}`);
  if (!list.ok) {
    notFound();
  }
  const t = await getTranslations("policies.exemptions");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: t("registerTitle"), href: exemptionRegisterHref },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("registerTitle")} description={t("registerDescription")} icon="policy" nav={<PoliciesNav current="exemptions" exemptions />} />
        <SectionNav
          label={t("filter")}
          items={filters.map((f) => ({ key: f, href: f === "live" ? exemptionRegisterHref : `${exemptionRegisterHref}?status=${f}`, label: t(`filters.${f}`) }))}
          current={chosen ?? "live"}
        />
        {list.data.length === 0 ? (
          <StateView kind="empty" title={t("registerEmpty")} message="" />
        ) : (
          <section className="policies-card" aria-label={t("registerTitle")}>
            <ExemptionList items={list.data} showPolicy />
          </section>
        )}
      </main>
    </>
  );
}
