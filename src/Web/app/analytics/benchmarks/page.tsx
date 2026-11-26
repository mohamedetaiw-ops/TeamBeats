import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { BenchmarksView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Benchmarks } from "@/features/analytics/Benchmarks";
import "@/features/analytics/analytics.css";

/** Benchmarks across anonymized organizations (spec 021 R3). The API answers 403 to anyone without analytics rights for the whole tenant. */
export default async function BenchmarksPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("analytics.benchmarks");
  const view = await apiGet<BenchmarksView>("/api/v1/analytics/benchmarks");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/benchmarks" }]} />
      <main id="main" className="page analytics-benchmarks">
        <PageHeader title={t("title")} description={t("intro")} icon="trend-up" />
        {view.ok ? (
          <Benchmarks view={view.data} />
        ) : (
          <StateView kind={view.status === 403 ? "denied" : "error"} title={t(view.status === 403 ? "denied" : "error")} message={t(view.status === 403 ? "deniedHint" : "errorHint")} />
        )}
      </main>
    </>
  );
}
