import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { scopeHref, type ScopeDashboardView } from "@/lib/analytics/types";
import type { LocationRef } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ScopeDashboard } from "@/features/analytics/ScopeDashboard";
import "@/features/analytics/analytics.css";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/**
 * One location dashboard (spec 021 T014, US6) for the last 30 days, at <c>?key=</c> since location keys are free text.
 * The API needs analytics.view for the whole tenant. Without a key the page says to pick one.
 */
export default async function ScopeLocationPage({ searchParams }: { searchParams: Promise<{ key?: string }> }) {
  const { key } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("analytics.scopes");
  const [view, locations] = await Promise.all([
    key ? apiGet<ScopeDashboardView>(`/api/v1/analytics/scopes/location/${encodeURIComponent(key)}`) : Promise.resolve(null),
    apiGet<LocationRef[]>("/api/v1/people/locations"),
  ]);
  if (view && !view.ok && view.status === 404) notFound();
  const locale = await getLocale();
  const located = key && locations.ok ? locations.data.find((l) => l.key === key) : undefined;
  const title = key ? (located ? pick(located.names, locale) || key : key) : t("title");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("workspace"), href: "/analytics" },
          { label: t("title"), href: "/analytics/scopes" },
          ...(key ? [{ label: title, href: scopeHref({ kind: "location", key }) }] : []),
        ]}
      />
      <main id="main" className="page analytics-scope">
        <PageHeader title={title} description={t("detailIntro")} icon="pin" />
        {view === null ? (
          <StateView kind="empty" title={t("pickTitle")} message={t("pickHint")} />
        ) : view.ok ? (
          <ScopeDashboard view={view.data} />
        ) : (
          <StateView kind="denied" title={t("denied")} message={t("deniedHint")} />
        )}
      </main>
    </>
  );
}
