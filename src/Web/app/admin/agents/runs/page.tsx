import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText, type AgentSummary, type RunSummary } from "@/lib/agents/types";
import { Button } from "@/components/ui/Button";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";

type Filters = { agent?: string; outcome?: string; tool?: string };

const OUTCOMES = ["answered", "limit_reached", "policy_blocked", "unavailable", "suspended", "stopped", "expired"] as const;
const GUID = /^[0-9a-f-]{36}$/i;
const TOOL = /^[a-z_]+\.[a-z_]+$/;

/**
 * Agent runs (spec 027 US6, T013): the person's own runs, and for security officers the runs of agents in their scope,
 * filtered by agent, outcome and tool. The API decides which runs the viewer may see.
 */
export default async function AgentRunsPage({ searchParams }: { searchParams: Promise<Filters> }) {
  await agentsSection();
  const filters = await searchParams;
  const query = new URLSearchParams({ limit: "100" });
  if (filters.agent && GUID.test(filters.agent)) query.set("agent", filters.agent);
  if (filters.outcome && (OUTCOMES as readonly string[]).includes(filters.outcome)) query.set("outcome", filters.outcome);
  if (filters.tool && TOOL.test(filters.tool)) query.set("tool", filters.tool);
  const [runs, agents] = await Promise.all([apiGet<{ runs: RunSummary[] }>(`/api/v1/agents/runs?${query.toString()}`), apiGet<{ agents: AgentSummary[] }>("/api/v1/agents")]);
  if (!runs.ok) notFound();
  const t = await getTranslations("agents");
  const locale = await getLocale();
  const format = await getFormatter();
  const names = new Map((agents.ok ? agents.data.agents : []).map((a) => [a.id, pickText(a.name, locale)]));
  return (
    <>
      <h1>{t("runs.title")}</h1>
      <AgentTabs current="runs" />
      <div className="agents">
        <form className="agents-section" method="get" aria-labelledby="agents-runs-filters">
          <h2 id="agents-runs-filters">{t("runs.filters")}</h2>
          <div className="agents-row">
            <div className="field">
              <label htmlFor="agents-runs-agent">{t("runs.agent")}</label>
              <select id="agents-runs-agent" name="agent" defaultValue={query.get("agent") ?? ""}>
                <option value="">{t("runs.any")}</option>
                {[...names.entries()].map(([id, name]) => (
                  <option key={id} value={id}>
                    {name}
                  </option>
                ))}
              </select>
            </div>
            <div className="field">
              <label htmlFor="agents-runs-outcome">{t("runs.outcome")}</label>
              <select id="agents-runs-outcome" name="outcome" defaultValue={query.get("outcome") ?? ""}>
                <option value="">{t("runs.any")}</option>
                {OUTCOMES.map((o) => (
                  <option key={o} value={o}>
                    {t(`outcomes.${o}`)}
                  </option>
                ))}
              </select>
            </div>
            <div className="field">
              <label htmlFor="agents-runs-tool">{t("runs.tool")}</label>
              <input id="agents-runs-tool" name="tool" type="text" dir="ltr" defaultValue={query.get("tool") ?? ""} />
            </div>
            <Button type="submit">{t("runs.apply")}</Button>
          </div>
        </form>
        <section className="agents-section" aria-labelledby="agents-runs-list">
          <h2 id="agents-runs-list">{t("runs.list")}</h2>
          {runs.data.runs.length === 0 ? (
            <p>{t("runs.empty")}</p>
          ) : (
            <div className="agents-table">
              <table data-testid="agents-runs">
                <thead>
                  <tr>
                    <th scope="col">{t("runs.when")}</th>
                    <th scope="col">{t("runs.agent")}</th>
                    <th scope="col">{t("runs.mode")}</th>
                    <th scope="col">{t("runs.outcome")}</th>
                    <th scope="col">{t("runs.toolCalls")}</th>
                    <th scope="col">{t("runs.denials")}</th>
                  </tr>
                </thead>
                <tbody>
                  {runs.data.runs.map((r) => (
                    <tr key={r.id}>
                      <td>
                        <Link href={`/admin/agents/runs/${r.id}`}>{format.dateTime(new Date(r.createdAt), { dateStyle: "medium", timeStyle: "short" })}</Link>
                      </td>
                      <td>
                        <bdi>{names.get(r.agentId) ?? t("runs.unknownAgent")}</bdi>
                      </td>
                      <td>{t(`runs.modes.${r.mode === "test" || r.mode === "eval" ? r.mode : "run"}`)}</td>
                      <td>{r.outcome && t.has(`outcomes.${r.outcome}`) ? t(`outcomes.${r.outcome}`) : t(`runStatus.${r.status}`)}</td>
                      <td data-number>{format.number(r.toolCalls)}</td>
                      <td data-number>{format.number(r.denials)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </div>
    </>
  );
}
