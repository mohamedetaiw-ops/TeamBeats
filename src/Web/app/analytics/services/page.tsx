import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AnalyticsService } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ServiceList } from "@/features/analytics/ServiceAnalytics";
import "@/features/analytics/analytics.css";

/** Service analytics (spec 021 T012, US4): the services the person owns. The API answers 403 to anyone who owns none. */
export default async function ServiceAnalyticsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const services = await apiGet<AnalyticsService[]>("/api/v1/analytics/services");
  const t = await getTranslations("analytics.services");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/services" }]} />
      <main id="main" className="page analytics-services">
        <PageHeader title={t("title")} description={t("intro")} icon="services" />
        {services.ok ? <ServiceList services={services.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
      </main>
    </>
  );
}
