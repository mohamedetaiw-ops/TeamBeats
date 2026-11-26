import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { regulatorChangesHref, type PolicyListItem, type RegisterRights, type RegulatorChangeStatus, type RegulatorChangesView } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { SectionNav } from "@/components/ui/SectionNav";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { RegulatorChangeList } from "@/features/policies/RegulatorChangeList";
import "@/features/policies/policies.css";

const filters = ["new", "actioned", "dismissed"] as const satisfies readonly RegulatorChangeStatus[];

/**
 * Regulator changes (spec 025 R3 US10): what the regulator feeds of the tenant published, new ones first, for the
 * people who manage policies. They mark an item as not relevant or link it to policies and start reviews. The API
 * refuses anyone else and answers "not available" when the package has no regulator feeds.
 */
export default async function RegulatorChangesPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  const { status } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const chosen = filters.find((f) => f === status) ?? "new";
  const [view, rights, policies] = await Promise.all([
    apiGet<RegulatorChangesView>(`/api/v1/policies/regulator-changes${chosen === "new" ? "" : `?status=${chosen}`}`),
    apiGet<RegisterRights>("/api/v1/policies/rights"),
    apiGet<PolicyListItem[]>("/api/v1/policies"),
  ]);
  if (!view.ok) {
    notFound();
  }
  const t = await getTranslations("policies.regulators");
  const r = rights.ok ? rights.data : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: t("title"), href: regulatorChangesHref },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader
          title={t("title")}
          description={t("description")}
          icon="policy"
          nav={<PoliciesNav current="regulators" exemptions={r?.exemptions === true} evidence={r?.evidence === true} team={r?.team === true} regulators />}
        />
        {!view.data.available ? (
          <StateView kind="denied" title={t("unavailable")} message={t("unavailableMessage")} />
        ) : (
          <>
            {view.data.feeds.length === 0 ? <StateView kind="empty" title={t("noFeeds")} message={t("noFeedsMessage")} /> : null}
            <SectionNav
              label={t("filter")}
              items={filters.map((f) => ({ key: f, href: f === "new" ? regulatorChangesHref : `${regulatorChangesHref}?status=${f}`, label: t(`filters.${f}`) }))}
              current={chosen}
            />
            {view.data.items.length === 0 ? (
              <StateView kind="empty" title={t(`empty.${chosen}`)} message="" />
            ) : (
              <RegulatorChangeList items={view.data.items} policies={policies.ok ? policies.data.filter((p) => p.status !== "retired") : []} />
            )}
          </>
        )}
      </main>
    </>
  );
}
