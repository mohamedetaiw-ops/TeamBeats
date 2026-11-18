import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AnalyticsCampaign } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CampaignList } from "@/features/analytics/CampaignAnalytics";
import "@/features/analytics/analytics.css";

/**
 * Campaign analytics (spec 021 T011, US2): the campaigns the person runs, two of which they can compare. The API lists
 * only campaigns Content lets them run and answers 403 without advanced analytics.
 */
export default async function CampaignAnalyticsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const campaigns = await apiGet<AnalyticsCampaign[]>("/api/v1/analytics/campaigns");
  const t = await getTranslations("analytics.campaigns");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/campaigns" }]} />
      <main id="main" className="page analytics-campaigns">
        <PageHeader title={t("title")} description={t("intro")} icon="megaphone" />
        {campaigns.ok ? <CampaignList campaigns={campaigns.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
      </main>
    </>
  );
}
