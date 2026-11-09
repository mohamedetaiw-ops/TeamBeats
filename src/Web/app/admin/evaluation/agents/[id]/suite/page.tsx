import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AgentSuites, MetricDefinition } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { SuiteEditor } from "@/features/evaluation/SuiteEditor";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

/** An agent's evaluation suites and datasets (spec 034 US1 and US2). An agent outside the person's scope is not found. */
export default async function EvaluationSuitePage({ params }: { params: Promise<{ id: string }> }) {
  await evaluationSection();
  const { id } = await params;
  const [suites, catalog] = await Promise.all([
    apiGet<AgentSuites>(`/api/v1/evaluation/agents/${encodeURIComponent(id)}/suites`),
    apiGet<MetricDefinition[]>("/api/v1/evaluation/metrics"),
  ]);
  if (!suites.ok) notFound();
  const t = await getTranslations("evaluation");
  return (
    <>
      <h1>{t("suite.title")}</h1>
      <EvaluationTabs current="agents" />
      <p>
        <Link href={`/admin/evaluation/agents/${suites.data.agentId}`}>{t("run.back")}</Link>
      </p>
      <SuiteEditor initial={suites.data} catalog={catalog.ok ? catalog.data : []} />
    </>
  );
}
