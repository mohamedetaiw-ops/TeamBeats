import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AlertSettings, SecurityAlert } from "@/lib/security/types";
import { AlertList } from "@/features/security/AlertList";
import { AlertSettingsForm } from "@/features/security/AlertSettingsForm";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** Security alerts and their thresholds (spec 026 US5, T016, T025). Plans without alerts get the API's refusal as a notice. */
export default async function SecurityAlertsPage() {
  const section = await securitySection();
  const [alerts, settings] = await Promise.all([
    apiGet<SecurityAlert[]>("/api/v1/security/alerts"),
    apiGet<AlertSettings>("/api/v1/security/alert-settings"),
  ]);
  const t = await getTranslations("security");
  return (
    <>
      <AdminHeader section="security" title={t("alerts.title")} nav={<SecurityTabs current="alerts" openAlerts={section.badge} />} />
      <p className="notice">{t("alerts.intro")}</p>
      {alerts.ok ? <AlertList initial={alerts.data} /> : <p className="notice notice-error">{t(alerts.status === 403 ? "alerts.unavailable" : "alerts.failed")}</p>}
      {settings.ok ? <AlertSettingsForm initial={settings.data} /> : null}
    </>
  );
}
