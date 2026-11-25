import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { keyName } from "@/lib/admin/keys";
import type { RoleView } from "@/lib/admin/types";
import type { KpiTileSetView } from "@/lib/analytics/types";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { KpiTilesForm } from "@/features/analytics/KpiTilesForm";
import "@/features/analytics/analytics.css";

/**
 * KPI tiles on location dashboards (spec 021 R3, US6 scenario 2), kept as versioned configuration. The permissions a
 * tile can require are the ones the workspace's roles grant; their names come from the catalogue.
 */
export default async function KpiTilesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "analytics")) notFound();
  const [view, roles] = await Promise.all([apiGet<KpiTileSetView>("/api/v1/analytics/kpi-tiles"), apiGet<RoleView[]>("/api/v1/authz/roles")]);
  const t = await getTranslations("analytics.kpiTiles");
  const admin = await getTranslations("admin");
  const keys = [...new Set((roles.ok ? roles.data : []).flatMap((r) => r.permissions))].sort();
  const permissions = keys.map((key) => ({ value: key, label: keyName(admin, "permissions", key) }));
  return (
    <div className="analytics-item" data-testid="kpi-tiles">
      <PageHeader title={t("title")} description={t("intro")} icon="settings" />
      {view.ok ? <KpiTilesForm view={view.data} permissions={permissions} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
    </div>
  );
}
