import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { TenantKey } from "@/lib/security/types";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { KeysAdmin } from "@/features/security/KeysAdmin";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Customer-managed keys (spec 026 US8, FR-007). Security officers see the key status; the API says whether this person
 * may change it and checks every change again. Plans without customer-managed keys get the API's refusal as a notice.
 */
export default async function SecurityKeysPage() {
  const section = await securitySection();
  const key = await apiGet<TenantKey>("/api/v1/security/keys");
  const t = await getTranslations("security");
  return (
    <>
      <AdminHeader section="security" title={t("keys.title")} nav={<SecurityTabs current="keys" openAlerts={section.badge} />} />
      <p className="notice">{t("keys.intro")}</p>
      {key.ok ? <KeysAdmin initial={key.data} /> : <p className="notice notice-error">{t(key.status === 403 ? "keys.unavailable" : "keys.failed")}</p>}
    </>
  );
}
