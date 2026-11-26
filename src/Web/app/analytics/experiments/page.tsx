import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { Experiment, ExperimentPage, ExperimentResults } from "@/lib/experiments";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ExperimentsPanel } from "@/features/analytics/Experiments";
import "@/features/analytics/analytics.css";

/**
 * Experience experiments (spec 021 R3, US9 scenario 8). The API decides who sees the list (tenant administrators and
 * page publishers of the whole tenant, in a package with experiments) and who sees results (analytics viewers).
 */
export default async function ExperimentsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("analytics.experiments");
  const [list, pages] = await Promise.all([
    apiGet<{ experiments: Experiment[] }>("/api/v1/experience/experiments"),
    apiGet<ExperimentPage[]>("/api/v1/experience/pages"),
  ]);
  const experiments = list.ok ? list.data.experiments : [];
  const results: Record<string, ExperimentResults> = {};
  const read = await Promise.all(experiments.filter((x) => x.status !== "proposed").map((x) => apiGet<ExperimentResults>(`/api/v1/analytics/experiments/${x.id}`)));
  for (const r of read) {
    if (r.ok) results[r.data.experimentId] = r.data;
  }
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/experiments" }]} />
      <main id="main" className="page analytics-experiments">
        <PageHeader title={t("title")} description={t("intro")} icon="trend-up" />
        {list.ok ? (
          <ExperimentsPanel experiments={experiments} pages={pages.ok ? pages.data : []} results={results} />
        ) : (
          <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />
        )}
      </main>
    </>
  );
}
