import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pickText, type AgentDetail, type ToolView } from "@/lib/agents/types";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { AgentEditor } from "@/features/agents/AgentEditor";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";

/** One agent (spec 020 US1 to US3, T014): its editor, test panel and lifecycle. An agent outside the person's scope is not found. */
export default async function AgentPage({ params }: { params: Promise<{ id: string }> }) {
  await agentsSection();
  const { id } = await params;
  const [detail, tools, units] = await Promise.all([
    apiGet<AgentDetail>(`/api/v1/agents/${encodeURIComponent(id)}`),
    apiGet<{ tools: ToolView[] }>("/api/v1/agents/tools"),
    listUnits(),
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
      <AgentEditor initial={detail.data} tools={tools.ok ? tools.data.tools : []} units={units.ok ? units.data : []} />
    </>
  );
}
