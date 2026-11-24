import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { ComparisonsView, DashboardView } from "@/lib/evaluation/types";
import { ComparisonsPanel } from "@/features/evaluation/ComparisonsPanel";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** An agent's online comparisons of a challenger against the champion (spec 034 US9 scenarios 2 and 3, FR-026). */
export default async function EvaluationComparisonsPage({ params }: { params: Promise<{ id: string }> }) {
  await evaluationSection();
  const { id } = await params;
  const agent = encodeURIComponent(id);
  const [dashboard, comparisons] = await Promise.all([
    apiGet<DashboardView>(`/api/v1/evaluation/agents/${agent}/dashboard`),
    apiGet<ComparisonsView>(`/api/v1/evaluation/agents/${agent}/comparisons`),
  ]);
  if (!dashboard.ok || !comparisons.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const d = dashboard.data;
  return (
    <>
      <AdminHeader section="evaluation" title={pickText(d.name, locale) || t("untitled")} nav={<EvaluationTabs current="agents" />} />
      <div className="agents">
        <p className="agents-row">
          <Link href={`/admin/evaluation/agents/${d.agentId}`}>{t("productionView.back")}</Link>
          <Link href={`/admin/evaluation/agents/${d.agentId}/production`}>{t("dashboard.production")}</Link>
        </p>
        <p>{t("comparisons.intro")}</p>
      </div>
      <ComparisonsPanel initial={comparisons.data} versions={d.versions.map((v) => v.version)} champion={d.currentVersion} />
    </>
  );
}
