import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { campaignHref, type CampaignView, type SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { Badge } from "@/components/ui/Badge";
import { StateView } from "@/components/ui/StateView";
import { CampaignCreateForm } from "@/features/content/Campaigns";
import { TileCard } from "@/components/ui/TileCard";
import "@/features/content/content.css";

const tones = { draft: "neutral", scheduled: "info", completed: "success", cancelled: "neutral" } as const;

/**
 * Campaigns across channels (spec 009 US8) for the people who publish at a site: the campaigns they may run and a form to
 * start one. The API lists only campaigns of sites where the person publishes, and answers 403 when the plan has none.
 */
export default async function CampaignsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [campaigns, sites] = await Promise.all([apiGet<CampaignView[]>("/api/v1/content/campaigns"), apiGet<SiteView[]>("/api/v1/content/sites")]);
  const t = await getTranslations("content.campaigns");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/content/campaigns" }]} />
      <main id="main" className="page content-manage">
        <h1>{t("title")}</h1>
        <p className="field-hint">{t("intro")}</p>
        {!campaigns.ok ? (
          <StateView kind="denied" title={t("denied")} message="" />
        ) : (
          <>
            <section aria-labelledby="campaign-list">
              <h2 id="campaign-list">{t("list")}</h2>
              {campaigns.data.length === 0 ? (
                <p className="field-hint">{t("empty")}</p>
              ) : (
                <ul className="ui-tiles">
                  {campaigns.data.map((c) => (
                    <li key={c.id} data-testid="campaign">
                      <TileCard
                        title={c.name}
                        href={campaignHref(c.id)}
                        icon="megaphone"
                        meta={
                          <>
                            <Badge tone={tones[c.status]}>{t(`status.${c.status}`)}</Badge>
                            <span>{t("stepCount", { count: c.steps.length })}</span>
                          </>
                        }
                      />
                    </li>
                  ))}
                </ul>
              )}
            </section>
            <CampaignCreateForm sites={sites.ok ? sites.data : []} />
          </>
        )}
      </main>
    </>
  );
}
