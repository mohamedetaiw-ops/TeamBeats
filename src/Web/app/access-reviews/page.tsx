import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AccessReviewItem } from "@/lib/security/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { AccessReviewList } from "@/features/security/AccessReviewList";
import "@/features/security/security.css";

/** Access reviews for the person asked to review (spec 026 FR-012). The API lists only their own items. */
export default async function AccessReviewsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("accessReviews");
  const mine = await apiGet<{ items: AccessReviewItem[] }>("/api/v1/access-reviews");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/access-reviews" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="lock" />
        {mine.ok ? <AccessReviewList initial={mine.data.items} /> : <StateView kind="error" title={t("failedTitle")} message={t("failed")} />}
      </main>
    </>
  );
}
