import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AreaKey, AreaView } from "@/lib/analytics/types";
import type { IconName } from "@/components/ui/Icon";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { AreaDashboard } from "@/features/analytics/AreaDashboard";
import { ReportActions } from "@/features/analytics/ReportActions";
import "@/features/analytics/analytics.css";

const paths: Record<AreaKey, string> = { knowledge: "/analytics/knowledge", engagement: "/analytics/engagement", hr: "/analytics/hr", security: "/analytics/security" };
const icons: Record<AreaKey, IconName> = { knowledge: "book", engagement: "people", hr: "leave", security: "lock" };

/** The page of one area dashboard (spec 021 section 2) for the last 30 days. The API decides access and what is hidden. */
export async function AreaPage({ area }: { area: AreaKey }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<AreaView>(`/api/v1/analytics/${area}`);
  const t = await getTranslations("analytics.areas");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t(`${area}.title`), href: paths[area] }]} />
      <main id="main" className={`page analytics-area analytics-${area}`}>
        <PageHeader title={t(`${area}.title`)} description={t(`${area}.intro`)} icon={icons[area]} actions={view.ok ? <ReportActions definition={{ dashboard: area }} /> : undefined} />
        {view.ok ? (
          <AreaDashboard view={view.data} />
        ) : (
          <StateView kind={view.status === 403 ? "denied" : "error"} title={t(view.status === 403 ? `${area}.denied` : "error")} message={t(view.status === 403 ? `${area}.deniedHint` : "errorHint")} />
        )}
      </main>
    </>
  );
}
