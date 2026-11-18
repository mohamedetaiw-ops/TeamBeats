import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ComplianceAnalyticsView } from "@/lib/analytics/types";
import type { LocationRef } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ComplianceDashboard } from "@/features/analytics/ComplianceAnalytics";
import "@/features/analytics/analytics.css";

/** Policy compliance analytics (spec 021 T013, US5). The API answers 403 to anyone without policy.manage or the package. */
export default async function ComplianceAnalyticsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [view, locations] = await Promise.all([
    apiGet<ComplianceAnalyticsView>("/api/v1/analytics/compliance"),
    apiGet<LocationRef[]>("/api/v1/people/locations"),
  ]);
  const t = await getTranslations("analytics.compliance");
  const names = Object.fromEntries((locations.ok ? locations.data : []).map((l) => [l.key, l.names]));
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/compliance" }]} />
      <main id="main" className="page analytics-compliance">
        <PageHeader title={t("title")} description={t("intro")} icon="policy" />
        {view.ok ? (
          <ComplianceDashboard view={view.data} locations={names} />
        ) : (
          <StateView kind={view.status === 403 ? "denied" : "error"} title={t(view.status === 403 ? "denied" : "error")} message={t(view.status === 403 ? "deniedHint" : "errorHint")} />
        )}
      </main>
    </>
  );
}
