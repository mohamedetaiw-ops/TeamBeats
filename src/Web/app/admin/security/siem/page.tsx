import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { SiemPage } from "@/lib/security/types";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { SiemAdmin } from "@/features/security/SiemAdmin";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";

/**
 * SIEM streaming (spec 026 US4). Security officers see the destinations; the API says whether this person may change
 * them and checks every change again. Plans without SIEM streaming get the API's refusal as a notice.
 */
export default async function SecuritySiemPage() {
  const section = await securitySection();
  const siem = await apiGet<SiemPage>("/api/v1/security/siem");
  const t = await getTranslations("security");
  return (
    <>
      <h1>{t("siem.title")}</h1>
      <SecurityTabs current="siem" openAlerts={section.badge} />
      <p className="notice">{t("siem.intro")}</p>
      {siem.ok ? (
        <SiemAdmin initial={siem.data.destinations} canManage={siem.data.canManage} />
      ) : (
        <p className="notice notice-error">{t(siem.status === 403 ? "siem.unavailable" : "siem.failed")}</p>
      )}
    </>
  );
}
