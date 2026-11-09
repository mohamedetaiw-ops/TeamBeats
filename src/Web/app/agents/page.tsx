import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pickText, type AgentChoice } from "@/lib/agents/types";
import { Header } from "@/components/shell/Header";
import "@/features/agents/agents.css";

/** The agents a person may use (spec 020 US6, US7): published agents whose audience holds them, from the API. From a contextual action it passes the item on to the agent the person picks. */
export default async function MyAgentsPage({ searchParams }: { searchParams: Promise<{ itemSource?: string; itemId?: string }> }) {
  const { itemSource, itemId } = await searchParams;
  // A contextual action passes the item on to the chosen agent; its page checks the values again.
  const forward = itemSource && itemId ? `?${new URLSearchParams({ itemSource, itemId }).toString()}` : "";
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) redirect("/signin");
  const t = await getTranslations("agents.use");
  const locale = await getLocale();
  const available = await apiGet<{ agents: AgentChoice[] }>("/api/v1/agents/available");
  const agents = available.ok ? available.data.agents : [];
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/agents" }]} />
      <main id="main" className="page">
        <h1>{t("title")}</h1>
        {forward && agents.length > 0 ? <p className="notice">{t("chooseForItem")}</p> : null}
        {agents.length === 0 ? (
          <p>{t("empty")}</p>
        ) : (
          <ul className="agents-grid" data-testid="agents-available">
            {agents.map((a) => (
              <li key={a.id} className="agents-section">
                <h2>
                  <Link href={`/agents/${a.id}${forward}`}>
                    <bdi>{pickText(a.name, locale)}</bdi>
                  </Link>
                </h2>
                <p>{pickText(a.description, locale)}</p>
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
