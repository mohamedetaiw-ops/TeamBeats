import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { SecurityPolicy } from "@/lib/security/types";
import { PolicyEditor } from "@/features/security/PolicyEditor";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** Security policy (spec 026 US1, US2, T016): network rules with the lock-out preview, MFA and session rules. */
export default async function SecurityPolicyPage() {
  const section = await securitySection();
  const policy = await apiGet<SecurityPolicy>("/api/v1/security/policy");
  if (!policy.ok) notFound();
  const t = await getTranslations("security");
  return (
    <>
      <AdminHeader section="security" nav={<SecurityTabs current="policy" openAlerts={section.badge} />} />
      <p className="notice">{t("policy.intro")}</p>
      <PolicyEditor initial={policy.data} etag={`"v${policy.data.version}"`} />
    </>
  );
}
