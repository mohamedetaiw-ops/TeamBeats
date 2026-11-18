import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { WorkspaceView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { ButtonLink } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { WorkspaceExplorer } from "@/features/analytics/WorkspaceExplorer";
import "@/features/analytics/analytics.css";

/**
 * The analytics workspace (spec 021 T010, US2): the dashboards the package opens, with filters and breakdowns. The API
 * decides scope (a unit viewer only sees their units), applies the minimum group size and answers 403 to anyone else.
 */
export default async function AnalyticsWorkspacePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const workspace = await apiGet<WorkspaceView>("/api/v1/analytics/workspace");
  const t = await getTranslations("analytics.workspace");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/analytics" }]} />
      <main id="main" className="page analytics-workspace">
        <PageHeader
          title={t("title")}
          description={t("intro")}
          icon="trend-up"
          actions={
            <ButtonLink href="/analytics/campaigns" icon="megaphone" size="sm">
              {t("campaignsLink")}
            </ButtonLink>
          }
        />
        {!workspace.ok ? (
          <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />
        ) : workspace.data.metrics.length === 0 ? (
          <StateView kind="empty" title={t("noDashboards")} message="" />
        ) : (
          <WorkspaceExplorer workspace={workspace.data} />
        )}
      </main>
    </>
  );
}
