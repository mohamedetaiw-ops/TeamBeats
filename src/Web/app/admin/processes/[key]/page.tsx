import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick } from "@/lib/services/types";
import type { ProcessAutonomy, ProcessModelView } from "@/lib/processes";
import type { ProcessEvalView } from "@/lib/evaluation/types";
import { AutonomyPanel } from "@/features/processes/AutonomyPanel";
import { ProcessEvaluationPanel } from "@/features/processes/ProcessEvaluationPanel";
import { Modeler } from "@/features/processes/Modeler";

/** One process model (spec 024 FR-001), with the approval of its agent tasks when it has some (spec 027 FR-024). A model the person may not design or read is not found. */
export default async function ProcessPage({ params }: { params: Promise<{ key: string }> }) {
  const { key } = await params;
  const [model, units, autonomy, evaluation] = await Promise.all([
    apiGet<ProcessModelView>(`/api/v1/workflow/processes/${encodeURIComponent(key)}`),
    listUnits(),
    // Only versions with agent tasks have an approval (spec 027 FR-024); the API answers nothing otherwise.
    apiGet<ProcessAutonomy | null>(`/api/v1/workflow/processes/${encodeURIComponent(key)}/autonomy`),
    // The end to end evaluation of its agent tasks (spec 034 R3); empty unless the person reads evaluations of those agents.
    apiGet<ProcessEvalView>(`/api/v1/evaluation/processes/${encodeURIComponent(key)}`),
  ]);
  if (!model.ok) {
    notFound();
  }
  const t = await getTranslations("processes.modeler");
  const locale = await getLocale();
  return (
    <>
      <p>
        <Link href="/admin/processes">{t("back")}</Link>
      </p>
      <h1>
        <bdi>{pick(model.data.names, locale, model.data.key)}</bdi>
      </h1>
      {autonomy.ok && autonomy.data ? <AutonomyPanel initial={autonomy.data} /> : null}
      {evaluation.ok && evaluation.data.versions.length > 0 ? <ProcessEvaluationPanel view={evaluation.data} /> : null}
      <Modeler processKey={model.data.key} initial={model.data} units={units.ok ? units.data : []} />
    </>
  );
}
