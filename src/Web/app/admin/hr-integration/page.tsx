import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { HrChange, HrOrgCode, HrOverview, HrRecordPage, HrRun } from "@/lib/integrations/types";
import { HrIntegrationAdmin } from "@/features/hr-integration/HrIntegrationAdmin";
import { HrLifecyclePanel } from "@/features/hr-integration/HrLifecyclePanel";

/**
 * HR integration (spec 019 section 10): last run and counts, held runs, records by state, links made by hand, the
 * matching rule and threshold, a sync now, the HR org codes and the lifecycle changes. The section appears only when the API lists it; when the plan lacks HR
 * integration the API refuses and the page says so. Every call is authorized again by the API.
 */
export default async function HrIntegrationPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "hr_integration")) {
    notFound();
  }
  const t = await getTranslations("hr_integration");
  const [overview, runs, records, codes, changes, units] = await Promise.all([
    apiGet<HrOverview>("/api/v1/hr/integration"),
    apiGet<HrRun[]>("/api/v1/hr/integration/runs"),
    apiGet<HrRecordPage>("/api/v1/hr/integration/records?state=unmatched"),
    apiGet<HrOrgCode[]>("/api/v1/hr/integration/org-codes"),
    apiGet<HrChange[]>("/api/v1/hr/integration/changes"),
    listUnits(),
  ]);
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      {overview.ok && runs.ok && records.ok ? (
        <>
          <HrIntegrationAdmin initial={overview.data} initialRuns={runs.data} initialRecords={records.data} />
          <HrLifecyclePanel initialCodes={codes.ok ? codes.data : []} initialChanges={changes.ok ? changes.data : []} units={units.ok ? units.data : []} />
        </>
      ) : (
        <p className="notice">{t("notInPackage")}</p>
      )}
    </>
  );
}
