import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { serviceAnalyticsHref, type ServiceAnalyticsView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ServiceDashboard } from "@/features/analytics/ServiceAnalytics";
import { ReportActions } from "@/features/analytics/ReportActions";
import "@/features/analytics/analytics.css";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** One service's analytics (spec 021 T012, US4) for the last 90 days. Services decides who may see it. */
export default async function ServiceAnalyticsDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<ServiceAnalyticsView>(`/api/v1/analytics/services/${encodeURIComponent(id)}`);
  if (!view.ok && view.status === 404) notFound();
  const t = await getTranslations("analytics.services");
  const locale = await getLocale();
  const title = view.ok ? pick(view.data.service.names, locale) : t("title");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("workspace"), href: "/analytics" },
          { label: t("title"), href: "/analytics/services" },
          { label: title, href: serviceAnalyticsHref(id) },
        ]}
      />
      <main id="main" className="page analytics-service">
        <PageHeader title={title} description={t("detailIntro")} icon="services" actions={view.ok ? <ReportActions definition={{ dashboard: "service", target: id }} /> : undefined} />
        {view.ok ? <ServiceDashboard view={view.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
      </main>
    </>
  );
}
