import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { SecurityAlert } from "@/lib/security/types";
import { AlertList } from "@/features/security/AlertList";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";

/** Security alerts (spec 026 US5, T016). Plans without alerts get the API's refusal as a notice. */
export default async function SecurityAlertsPage() {
  const section = await securitySection();
  const alerts = await apiGet<SecurityAlert[]>("/api/v1/security/alerts");
  const t = await getTranslations("security");
  return (
    <>
      <h1>{t("alerts.title")}</h1>
      <SecurityTabs current="alerts" openAlerts={section.badge} />
      <p className="notice">{t("alerts.intro")}</p>
      {alerts.ok ? <AlertList initial={alerts.data} /> : <p className="notice notice-error">{t(alerts.status === 403 ? "alerts.unavailable" : "alerts.failed")}</p>}
    </>
  );
}
