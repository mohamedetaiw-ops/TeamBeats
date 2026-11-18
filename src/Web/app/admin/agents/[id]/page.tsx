import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pickText, type AccessRequestView, type AgentAnalyticsView, type AgentDetail, type StudioOffer, type ToolView } from "@/lib/agents/types";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { AgentEditor } from "@/features/agents/AgentEditor";
import { AgentAnalytics } from "@/features/agents/AgentAnalytics";
import { AccessRequests } from "@/features/agents/AccessRequests";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";

/** One agent (spec 020 US1 to US3, T014, FR-018): its editor, test panel, lifecycle and basic analytics. An agent outside the person's scope is not found. */
export default async function AgentPage({ params }: { params: Promise<{ id: string }> }) {
  await agentsSection();
  const { id } = await params;
  const [detail, tools, units, analytics, studio, access] = await Promise.all([
    apiGet<AgentDetail>(`/api/v1/agents/${encodeURIComponent(id)}`),
    apiGet<{ tools: ToolView[] }>("/api/v1/agents/tools"),
    listUnits(),
    apiGet<AgentAnalyticsView>(`/api/v1/agents/${encodeURIComponent(id)}/analytics`),
    apiGet<StudioOffer>("/api/v1/agents/studio"),
    // Listed only to the agent's owners; anyone else gets not found and sees no section.
    apiGet<{ requests: AccessRequestView[] }>(`/api/v1/agents/${encodeURIComponent(id)}/access-requests`),
  ]);
  if (!detail.ok) notFound();
  const t = await getTranslations("agents");
  const locale = await getLocale();
  return (
    <>
      <h1>
        <bdi>{pickText(detail.data.definition.name, locale) || t("editor.untitled")}</bdi>
      </h1>
      <AgentTabs current="agents" />
      <AgentEditor initial={detail.data} tools={tools.ok ? tools.data.tools : []} units={units.ok ? units.data : []} studio={studio.ok ? studio.data : null} />
      {access.ok ? <AccessRequests requests={access.data.requests} /> : null}
      {analytics.ok ? <AgentAnalytics view={analytics.data} /> : null}
    </>
  );
}
