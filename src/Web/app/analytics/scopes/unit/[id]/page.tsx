import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { scopeHref, type ScopeDashboardView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ScopeDashboard } from "@/features/analytics/ScopeDashboard";
import { ReportActions } from "@/features/analytics/ReportActions";
import "@/features/analytics/analytics.css";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** One unit dashboard with its subtree (spec 021 T014, US6) for the last 30 days. The API needs analytics.view at the unit. */
export default async function ScopeUnitPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<ScopeDashboardView>(`/api/v1/analytics/scopes/unit/${encodeURIComponent(id)}`);
  if (!view.ok && view.status === 404) notFound();
  const t = await getTranslations("analytics.scopes");
  const locale = await getLocale();
  const title = view.ok ? pick(view.data.scope.names, locale) : t("title");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("workspace"), href: "/analytics" },
          { label: t("title"), href: "/analytics/scopes" },
          { label: title, href: scopeHref({ kind: "unit", key: id }) },
        ]}
      />
      <main id="main" className="page analytics-scope">
        <PageHeader title={title} description={t("detailIntro")} icon="building" actions={view.ok ? <ReportActions definition={{ dashboard: "scope_unit", target: id }} /> : undefined} />
        {view.ok ? <ScopeDashboard view={view.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
      </main>
    </>
  );
}
