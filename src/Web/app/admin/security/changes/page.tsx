import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { FourEyesSetting, PendingChange } from "@/lib/security/types";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { FourEyesAdmin } from "@/features/security/FourEyesAdmin";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** Four-eyes approval of critical settings (spec 026 FR-016). The API authorizes the setting, the list and every decision. */
export default async function SecurityChangesPage() {
  const section = await securitySection();
  const [setting, changes] = await Promise.all([
    apiGet<FourEyesSetting>("/api/v1/security/four-eyes"),
    apiGet<{ items: PendingChange[] }>("/api/v1/security/changes"),
  ]);
  const t = await getTranslations("security");
  return (
    <>
      <AdminHeader section="security" title={t("changes.title")} nav={<SecurityTabs current="changes" openAlerts={section.badge} />} />
      <p className="notice">{t("changes.intro")}</p>
      <FourEyesAdmin setting={setting.ok ? setting.data : null} changes={changes.ok ? changes.data.items : []} />
    </>
  );
}
