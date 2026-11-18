import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AiUsageView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { AiUsageDashboard } from "@/features/analytics/AiUsageAnalytics";
import { ReportActions } from "@/features/analytics/ReportActions";
import "@/features/analytics/analytics.css";

/** AI usage (spec 021 FR-016) for the last 30 days. The API needs analytics.advanced and analytics.view for the whole tenant. */
export default async function AiUsageAnalyticsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<AiUsageView>("/api/v1/analytics/ai-usage");
  const t = await getTranslations("analytics.aiUsage");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/ai-usage" }]} />
      <main id="main" className="page analytics-ai-usage">
        <PageHeader title={t("title")} description={t("intro")} icon="spark" actions={view.ok ? <ReportActions definition={{ dashboard: "ai_usage" }} /> : undefined} />
        {view.ok ? (
          <AiUsageDashboard view={view.data} />
        ) : (
          <StateView kind={view.status === 403 ? "denied" : "error"} title={t(view.status === 403 ? "denied" : "error")} message={t(view.status === 403 ? "deniedHint" : "errorHint")} />
        )}
      </main>
    </>
  );
}
