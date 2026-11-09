import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { RunRecord } from "@/lib/agents/types";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";

/**
 * The run timeline (spec 027 US6, FR-016, T013): every model turn, tool call and confirmation in order with the
 * gateway's decision and reason, the routing decision, the item a contextual action worked on, and the cited sources.
 * Parameters are masked by the API when the run carried confidential data.
 */
export default async function AgentRunPage({ params }: { params: Promise<{ id: string }> }) {
  await agentsSection();
  const { id } = await params;
  const record = await apiGet<RunRecord>(`/api/v1/agents/runs/${encodeURIComponent(id)}`);
  if (!record.ok) notFound();
  const t = await getTranslations("agents");
  const format = await getFormatter();
  const { run, steps, sources, route } = record.data;
  const toolLabel = (name: string) => (t.has(`toolNames.${name}`) ? t(`toolNames.${name}`) : name);
  return (
    <>
      <h1>{t("timeline.title")}</h1>
      <AgentTabs current="runs" />
      <div className="agents">
        <section className="agents-section" aria-labelledby="agents-run-summary">
          <h2 id="agents-run-summary">{t("timeline.summary")}</h2>
          <p>
            {t("timeline.facts", {
              when: format.dateTime(new Date(run.createdAt), { dateStyle: "medium", timeStyle: "short" }),
              version: String(run.agentVersion),
              outcome: run.outcome && t.has(`outcomes.${run.outcome}`) ? t(`outcomes.${run.outcome}`) : t(`runStatus.${run.status}`),
            })}
          </p>
          {route ? <p data-testid="agents-run-route">{t(`timeline.route.${route.method}`, { candidates: String(route.candidates) })}</p> : null}
          {record.data.itemSource ? <p>{t("timeline.item")}</p> : null}
        </section>
        <section className="agents-section" aria-labelledby="agents-run-steps">
          <h2 id="agents-run-steps">{t("timeline.steps")}</h2>
          <ol className="agents-timeline" data-testid="agents-run-steps">
            {steps.map((s) => (
              <li key={s.n}>
                {t(`timeline.kinds.${s.kind}`, { tool: s.tool ? toolLabel(s.tool) : "" })}: {t.has(`decisions.${s.decision}`) ? t(`decisions.${s.decision}`) : s.decision}
                {s.reason ? (
                  <>
                    {" "}
                    <span className="agents-badge">
                      <bdi>{s.reason}</bdi>
                    </span>
                  </>
                ) : null}{" "}
                <span className="field-hint">{t("timeline.duration", { ms: format.number(s.durationMs) })}</span>
              </li>
            ))}
          </ol>
        </section>
        {sources.length > 0 ? (
          <section className="agents-section" aria-labelledby="agents-run-sources">
            <h2 id="agents-run-sources">{t("timeline.sources")}</h2>
            <ol>
              {sources.map((c) => (
                <li key={c.n}>
                  <Link href={c.link}>
                    <bdi>{c.title}</bdi>
                  </Link>
                </li>
              ))}
            </ol>
          </section>
        ) : null}
      </div>
    </>
  );
}
