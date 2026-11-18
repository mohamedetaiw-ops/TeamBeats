import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { CampaignComparison } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ButtonLink } from "@/components/ui/Button";
import { CampaignDashboard } from "@/features/analytics/CampaignAnalytics";
import "@/features/analytics/analytics.css";

/** Two campaigns side by side (spec 021 US2 scenario 3). Each keeps its own rights and minimum group size. */
export default async function CampaignComparePage({ searchParams }: { searchParams: Promise<{ first?: string; second?: string }> }) {
  const { first, second } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("analytics.campaigns");
  const comparison =
    first && second
      ? await apiGet<CampaignComparison>(`/api/v1/analytics/campaigns/compare?first=${encodeURIComponent(first)}&second=${encodeURIComponent(second)}`)
      : null;
  if (comparison && !comparison.ok && comparison.status === 404) notFound();
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("workspace"), href: "/analytics" },
          { label: t("title"), href: "/analytics/campaigns" },
          { label: t("compareTitle"), href: "/analytics/campaigns/compare" },
        ]}
      />
      <main id="main" className="page analytics-compare">
        <PageHeader title={t("compareTitle")} description={t("compareIntro")} icon="megaphone" />
        {!comparison ? (
          <StateView
            kind="empty"
            title={t("comparePick")}
            message={t("compareHint")}
            action={
              <ButtonLink href="/analytics/campaigns" size="sm">
                {t("title")}
              </ButtonLink>
            }
          />
        ) : comparison.ok ? (
          <div className="analytics-compare-grid">
            {[comparison.data.first, comparison.data.second].map((view) => (
              <section key={view.campaign.id} aria-labelledby={`compare-${view.campaign.id}`}>
                <h2 id={`compare-${view.campaign.id}`}>{view.campaign.name}</h2>
                <CampaignDashboard view={view} headingLevel={3} />
              </section>
            ))}
          </div>
        ) : (
          <StateView kind={comparison.status === 400 ? "error" : "denied"} title={comparison.status === 400 ? t("compareInvalid") : t("denied")} message="" />
        )}
      </main>
    </>
  );
}
