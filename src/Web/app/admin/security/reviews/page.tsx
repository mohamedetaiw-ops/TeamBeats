import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AccessReviewsPage } from "@/lib/security/types";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { AccessReviewAdmin } from "@/features/security/AccessReviewAdmin";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** Access reviews (spec 026 FR-012) for security administrators and officers. The API authorizes the data and every change. */
export default async function SecurityReviewsPage() {
  const section = await securitySection();
  const reviews = await apiGet<AccessReviewsPage>("/api/v1/security/access-reviews");
  const t = await getTranslations("security");
  return (
    <>
      <AdminHeader section="security" title={t("reviews.title")} nav={<SecurityTabs current="reviews" openAlerts={section.badge} />} />
      <p className="notice">{t("reviews.intro")}</p>
      {reviews.ok ? <AccessReviewAdmin initial={reviews.data} /> : <p className="notice notice-error">{t("reviews.failed")}</p>}
    </>
  );
}
