import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { ProductionPolicyView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { ProductionPolicyEditor } from "@/features/evaluation/ProductionPolicyEditor";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** The tenant's production evaluation policy (spec 034 US7, FR-023). Each agent's production scores are on its dashboard. */
export default async function EvaluationProductionPolicyPage() {
  await evaluationSection();
  const policy = await apiGet<ProductionPolicyView>("/api/v1/evaluation/production/policy");
  if (!policy.ok) notFound();
  const t = await getTranslations("evaluation");
  return (
    <>
      <AdminHeader section="evaluation" title={t("production.title")} nav={<EvaluationTabs current="production" />} />
      <ProductionPolicyEditor initial={policy.data} />
    </>
  );
}
