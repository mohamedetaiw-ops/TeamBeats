import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AgentChoice } from "@/lib/agents/types";

/**
 * The contextual action on a page, policy or document (spec 020 US7, FR-012): a link to choose an agent that works
 * with this item. It shows only when the API lists an agent the person may use; the API reads the item again, with
 * the person's permissions, when the agent runs.
 */
export async function AskAgentLink({ source, itemId }: { source: "content" | "policies" | "documents"; itemId: string }) {
  const available = await apiGet<{ agents: AgentChoice[] }>("/api/v1/agents/available");
  if (!available.ok || available.data.agents.length === 0) return null;
  const t = await getTranslations("agents.use");
  const query = new URLSearchParams({ itemSource: source, itemId });
  return (
    <p>
      <Link href={`/agents?${query.toString()}`}>{t("ask")}</Link>
    </p>
  );
}
