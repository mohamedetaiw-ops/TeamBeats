import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { campaignAnalyticsHref, type CampaignAnalyticsView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CampaignDashboard } from "@/features/analytics/CampaignAnalytics";
import "@/features/analytics/analytics.css";

/** One campaign's analytics (spec 021 T011, US2). Content decides who may see it: anyone else gets not found. */
export default async function CampaignAnalyticsDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<CampaignAnalyticsView>(`/api/v1/analytics/campaigns/${encodeURIComponent(id)}`);
  if (!view.ok && view.status === 404) notFound();
  const t = await getTranslations("analytics.campaigns");
  const title = view.ok ? view.data.campaign.name : t("title");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("workspace"), href: "/analytics" },
          { label: t("title"), href: "/analytics/campaigns" },
          { label: title, href: campaignAnalyticsHref(id) },
        ]}
      />
      <main id="main" className="page analytics-campaign">
        <PageHeader title={title} description={t("detailIntro")} icon="megaphone" />
        {view.ok ? <CampaignDashboard view={view.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
      </main>
    </>
  );
}
