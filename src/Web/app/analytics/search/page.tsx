import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { SearchGapsView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { SearchGapsDashboard } from "@/features/analytics/SearchGaps";
import { ReportActions } from "@/features/analytics/ReportActions";
import "@/features/analytics/analytics.css";

/** Search gaps (spec 021 US3) for the last 30 days. The API needs analytics.advanced and tenant wide analytics.view, search.manage or kb.edit. */
export default async function SearchGapsPage({ searchParams }: { searchParams: Promise<{ locale?: string; unit?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { locale, unit } = await searchParams;
  const params = new URLSearchParams();
  if (locale) params.set("locale", locale);
  if (unit) params.set("unit", unit);
  const query = params.toString();
  const view = await apiGet<SearchGapsView>(`/api/v1/analytics/search${query ? `?${query}` : ""}`);
  const t = await getTranslations("analytics.search");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/search" }]} />
      <main id="main" className="page analytics-search">
        <PageHeader
          title={t("title")}
          description={t("intro")}
          icon="search"
          actions={view.ok ? <ReportActions definition={{ dashboard: "search", filters: view.data.locale || view.data.unitId ? { locale: view.data.locale ?? undefined, unitId: view.data.unitId ?? undefined } : null }} /> : undefined}
        />
        {view.ok ? (
          <SearchGapsDashboard view={view.data} locales={me.data.tenant.enabledLocales} />
        ) : (
          <StateView kind={view.status === 403 ? "denied" : "error"} title={t(view.status === 403 ? "denied" : "error")} message={t(view.status === 403 ? "deniedHint" : "errorHint")} />
        )}
      </main>
    </>
  );
}
