import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AgentPolicyView, KillSwitchView, ToolView, VerifiedAiClient } from "@/lib/agents/types";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { AgentPolicyEditor } from "@/features/agents/AgentPolicyEditor";
import { KillSwitch } from "@/features/agents/KillSwitch";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";

/**
 * The tenant agent policy and kill switch (spec 027 US7, US8, T013). Designers and overseers read the policy; only the
 * AI policy administrator changes it. The kill switch shows only to people the API lets read it.
 */
export default async function AgentPolicyPage() {
  await agentsSection();
  const [policy, tools, kill, verified] = await Promise.all([
    apiGet<AgentPolicyView>("/api/v1/agents/policy"),
    apiGet<{ tools: ToolView[] }>("/api/v1/agents/tools"),
    apiGet<KillSwitchView>("/api/v1/agents/kill-switch"),
    apiGet<{ items: VerifiedAiClient[] }>("/api/v1/api-access/verified-mcp-clients"),
  ]);
  if (!policy.ok) notFound();
  const t = await getTranslations("agents.policy");
  return (
    <>
      <h1>{t("title")}</h1>
      <AgentTabs current="policy" />
      <div className="agents">
        {kill.ok ? <KillSwitch initial={kill.data} /> : null}
        <AgentPolicyEditor initial={policy.data} tools={tools.ok ? tools.data.tools : []} verified={verified.ok ? verified.data.items : []} />
      </div>
    </>
  );
}
