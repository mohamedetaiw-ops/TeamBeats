import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { RegisterRights } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { NewPolicyForm } from "@/features/policies/NewPolicyForm";
import { PageHeader } from "@/components/ui/PageHeader";

/** A new policy in the register, for compliance officers (spec 025 US1). */
export default async function NewPolicyPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const rights = await apiGet<RegisterRights>("/api/v1/policies/rights");
  if (!rights.ok || !rights.data.create) {
    notFound();
  }
  const t = await getTranslations("policies.create");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: t("title"), href: "/policies/new" },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("title")} icon="policy" nav={<PoliciesNav current={null} />} />
        <NewPolicyForm locales={me.data.tenant.enabledLocales} />
      </main>
    </>
  );
}
