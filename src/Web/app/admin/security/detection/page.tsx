import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { DetectionSettings } from "@/lib/security/types";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { DetectionAdmin } from "@/features/security/DetectionAdmin";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** Sensitive data detection (spec 026 US6). Plans without it get the API's refusal as a notice; the API authorizes every change. */
export default async function SecurityDetectionPage() {
  const section = await securitySection();
  const detection = await apiGet<DetectionSettings>("/api/v1/security/detection");
  const t = await getTranslations("security");
  return (
    <>
      <AdminHeader section="security" title={t("detection.title")} nav={<SecurityTabs current="detection" openAlerts={section.badge} />} />
      <p className="notice">{t("detection.intro")}</p>
      {detection.ok ? (
        <DetectionAdmin initial={detection.data} />
      ) : (
        <p className="notice notice-error">{t(detection.status === 403 ? "detection.unavailable" : "detection.failed")}</p>
      )}
    </>
  );
}
