import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { ProcessModelSummary } from "@/lib/processes";
import { ProcessOperationsTabs } from "@/features/processes/ProcessOperationsTabs";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Process operations (spec 024 US5, FR-013, FR-014): the operator view of instances and incidents, and monitoring per
 * process version. The section appears only when the API lists it, which needs the BPMN capability and
 * `process.operate` or `process.analyze`. Every call is authorized again by the API. `?tab=monitoring&process=key` opens the
 * monitoring of one process, as service analytics link to it (spec 021 US4 scenario 2).
 */
export default async function ProcessOperationsPage({ searchParams }: { searchParams: Promise<{ tab?: string; process?: string }> }) {
  const { tab, process } = await searchParams;
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "process_operations")) {
    notFound();
  }
  const processes = await apiGet<ProcessModelSummary[]>("/api/v1/workflow/processes");
  const t = await getTranslations("processes.operations");
  return (
    <>
      <AdminHeader section="process_operations" description={t("pageIntro")} />
      <div className="processes">
        <ProcessOperationsTabs processes={processes.ok ? processes.data : []} initialTab={tab} initialProcess={process} />
      </div>
    </>
  );
}
