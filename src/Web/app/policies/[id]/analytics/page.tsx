import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, policyHref } from "@/lib/policies/types";
import { itemAnalyticsApi, type ItemAnalyticsView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { ItemAnalyticsPanel } from "@/features/analytics/ItemAnalyticsPanel";

/**
 * Reach and attestation of a policy (spec 021 US1, spec 025), for its owner and compliance officers. While a campaign
 * is open the audience is the campaign's and acknowledgement is attestation.
 */
export default async function PolicyAnalyticsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<ItemAnalyticsView>(itemAnalyticsApi("policies", id));
  if (!view.ok && view.status !== 403) {
    notFound();
  }
  const t = await getTranslations("analytics.item");
  const tp = await getTranslations("policies.reading");
  const locale = await getLocale();
  const name = view.ok ? nameIn(view.data.titles, locale) : "";
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: tp("register"), href: "/policies" },
          ...(view.ok ? [{ label: name, href: policyHref(id) }] : []),
          { label: t("title"), href: `${policyHref(id)}/analytics` },
        ]}
      />
      <main id="main" className="page policies-page">
        <PoliciesNav current={null} />
        {view.ok ? (
          <>
            <h1>
              {t("heading")} <bdi>{name}</bdi>
            </h1>
            <ItemAnalyticsPanel source="policies" view={view.data} />
          </>
        ) : (
          <StateView kind="denied" message={t("denied")} />
        )}
      </main>
    </>
  );
}
