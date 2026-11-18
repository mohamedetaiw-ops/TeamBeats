import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { ToolView } from "@/lib/agents/types";
import type { OrgUnitView } from "@/lib/admin/types";
import { PageHeader } from "@/components/ui/PageHeader";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { UnitAgentPolicyEditor } from "@/features/agents/UnitAgentPolicyEditor";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";

/** Org unit agent policies (spec 027 US8 scenarios 3 and 4, T016). The API answers only for units in the viewer's scope. */
export default async function UnitAgentPolicyPage() {
  await agentsSection();
  const [units, tools] = await Promise.all([apiGet<OrgUnitView[]>("/api/v1/org/units"), apiGet<{ tools: ToolView[] }>("/api/v1/agents/tools")]);
  if (!units.ok) notFound();
  const t = await getTranslations("agents.unitPolicy");
  return (
    <>
      <PageHeader title={t("title")} nav={<AgentTabs current="units" />} />
      <UnitAgentPolicyEditor units={units.data.filter((u) => u.status === "active")} tools={tools.ok ? tools.data.tools : []} />
    </>
  );
}
