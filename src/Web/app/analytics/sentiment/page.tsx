import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AnalyticsPolicyView, SentimentView } from "@/lib/analytics/types";
import type { LocationRef } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { SentimentDashboard, SentimentSwitch } from "@/features/analytics/SentimentAnalytics";
import { ReportActions } from "@/features/analytics/ReportActions";
import "@/features/analytics/analytics.css";

/** Sentiment (spec 021 T017, US8). The API answers 403 without analytics.sentiment or tenant wide analytics.view. */
export default async function SentimentPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [view, policy, locations] = await Promise.all([
    apiGet<SentimentView>("/api/v1/analytics/sentiment"),
    apiGet<AnalyticsPolicyView>("/api/v1/analytics/policy"),
    apiGet<LocationRef[]>("/api/v1/people/locations"),
  ]);
  const t = await getTranslations("analytics.sentiment");
  const names = Object.fromEntries((locations.ok ? locations.data : []).map((l) => [l.key, l.names]));
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/sentiment" }]} />
      <main id="main" className="page analytics-sentiment">
        <PageHeader
          title={t("title")}
          description={t("intro")}
          icon="chat"
          actions={view.ok && view.data.enabled ? <ReportActions definition={{ dashboard: "sentiment" }} /> : undefined}
        />
        {view.ok && policy.ok && policy.data.canChange && policy.data.sentimentAvailable ? <SentimentSwitch policy={policy.data} /> : null}
        {view.ok ? (
          <SentimentDashboard view={view.data} locations={names} />
        ) : (
          <StateView kind={view.status === 403 ? "denied" : "error"} title={t(view.status === 403 ? "denied" : "error")} message={t(view.status === 403 ? "deniedHint" : "errorHint")} />
        )}
      </main>
    </>
  );
}
