import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { EffortEstimatesView } from "@/lib/analytics/types";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { EstimatesForm } from "@/features/analytics/EstimatesForm";
import "@/features/analytics/analytics.css";

/** The tenant's effort estimates for return on effort (spec 021 T011, US2 scenario 2), kept as versioned configuration. */
export default async function EstimatesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "analytics")) notFound();
  const view = await apiGet<EffortEstimatesView>("/api/v1/analytics/estimates");
  const t = await getTranslations("analytics.estimates");
  return (
    <div className="analytics-item" data-testid="estimates">
      <PageHeader title={t("title")} description={t("intro")} icon="settings" />
      {view.ok ? <EstimatesForm view={view.data} /> : <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />}
    </div>
  );
}
