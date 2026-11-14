import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { PageHeader } from "@/components/ui/PageHeader";
import type { BudgetsView, ModelPolicyView } from "@/lib/ai/types";
import type { AgentSummary } from "@/lib/agents/types";
import type { OrgUnitView } from "@/lib/admin/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { Budgets } from "@/features/ai/Budgets";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** AI budgets (spec 013 US5, T020). Units, agents and allowed models only fill the form's choices; the API decides. */
export default async function AiBudgetsPage() {
  await aiSection();
  const [budgets, policy, units, agents] = await Promise.all([
    apiGet<BudgetsView>("/api/v1/ai/budgets"),
    apiGet<ModelPolicyView>("/api/v1/ai/policy"),
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
    apiGet<{ agents: AgentSummary[] }>("/api/v1/agents"),
  ]);
  if (!budgets.ok) notFound();
  const t = await getTranslations("ai");
  return (
    <>
      <PageHeader title={t("budgets.title")} nav={<AiTabs current="budgets" />} />
      <Budgets
        initial={budgets.data}
        models={policy.ok ? policy.data.policy.allowlist : []}
        units={units.ok ? units.data : []}
        agents={agents.ok ? agents.data.agents : []}
      />
    </>
  );
}
