import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { ProcessModelSummary } from "@/lib/processes";
import { ProcessOperationsTabs } from "@/features/processes/ProcessOperationsTabs";

/**
 * Process operations (spec 024 US5, FR-013, FR-014): the operator view of instances and incidents, and monitoring per
 * process version. The section appears only when the API lists it, which needs the BPMN capability and
 * `process.operate` or `process.analyze`. Every call is authorized again by the API.
 */
export default async function ProcessOperationsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "process_operations")) {
    notFound();
  }
  const processes = await apiGet<ProcessModelSummary[]>("/api/v1/workflow/processes");
  const t = await getTranslations("processes.operations");
  return (
    <>
      <h1>{t("pageTitle")}</h1>
      <p className="notice">{t("pageIntro")}</p>
      <div className="processes">
        <ProcessOperationsTabs processes={processes.ok ? processes.data : []} />
      </div>
    </>
  );
}
