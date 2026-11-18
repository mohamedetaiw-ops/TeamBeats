import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ScopeRef } from "@/lib/analytics/types";
import type { LocationRef } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ScopeList } from "@/features/analytics/ScopeDashboard";
import "@/features/analytics/analytics.css";

/** Location and unit dashboards (spec 021 T014, US6): the units and locations the person may open. */
export default async function ScopesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [scopes, locations] = await Promise.all([
    apiGet<{ units: ScopeRef[]; locations: ScopeRef[] }>("/api/v1/analytics/scopes"),
    apiGet<LocationRef[]>("/api/v1/people/locations"),
  ]);
  const t = await getTranslations("analytics.scopes");
  const names = Object.fromEntries((locations.ok ? locations.data : []).map((l) => [l.key, l.names]));
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("workspace"), href: "/analytics" }, { label: t("title"), href: "/analytics/scopes" }]} />
      <main id="main" className="page analytics-scopes">
        <PageHeader title={t("title")} description={t("intro")} icon="building" />
        {scopes.ok ? (
          <>
            <ScopeList scopes={scopes.data.units} caption={t("units")} locationNames={names} />
            {scopes.data.locations.length > 0 ? <ScopeList scopes={scopes.data.locations} caption={t("locations")} locationNames={names} /> : null}
          </>
        ) : (
          <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />
        )}
      </main>
    </>
  );
}
