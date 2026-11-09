import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pickText, type AgentChoice } from "@/lib/agents/types";
import { Header } from "@/components/shell/Header";
import { AgentConversation } from "@/features/agents/AgentConversation";
import "@/features/agents/agents.css";

const SOURCES = ["content", "policies", "documents"] as const;
const GUID = /^[0-9a-f-]{36}$/i;

/**
 * A conversation with one agent (spec 020 US7, T014). With itemSource and itemId it is a contextual action on that
 * page, policy or document, which the API reads with the person's permissions. Agents the person may not use are not found.
 */
export default async function AgentConversationPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ itemSource?: string; itemId?: string }>;
}) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) redirect("/signin");
  const { id } = await params;
  const { itemSource, itemId } = await searchParams;
  const available = await apiGet<{ agents: AgentChoice[] }>("/api/v1/agents/available");
  const agent = available.ok ? available.data.agents.find((a) => a.id === id) : undefined;
  if (!agent) notFound();
  const t = await getTranslations("agents.use");
  const locale = await getLocale();
  const name = pickText(agent.name, locale);
  const item =
    itemSource && (SOURCES as readonly string[]).includes(itemSource) && itemId && GUID.test(itemId) ? { source: itemSource, id: itemId } : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/agents" },
          { label: name, href: `/agents/${id}` },
        ]}
      />
      <main id="main" className="page">
        <h1>
          <bdi>{name}</bdi>
        </h1>
        <p>{pickText(agent.description, locale)}</p>
        <AgentConversation agentId={id} item={item} />
      </main>
    </>
  );
}
