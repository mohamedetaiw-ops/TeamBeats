import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { campaignHref, pickName, type CampaignCounts, type CampaignDashboard, type CampaignView, type ItemView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { Badge } from "@/components/ui/Badge";
import { CampaignPlan } from "@/features/content/Campaigns";
import "@/features/content/content.css";

/**
 * One campaign (spec 009 US8): its plan, and its dashboard per step and in total with the breakdown by unit and location.
 * Counts of what people did show only for groups of at least the minimum group size (spec 021); smaller groups are
 * merged into "other" by the API.
 */
export default async function CampaignPage({ params }: { params: Promise<{ id: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { id } = await params;
  const campaign = await apiGet<CampaignView>(`/api/v1/content/campaigns/${encodeURIComponent(id)}`);
  if (!campaign.ok) {
    notFound();
  }
  const c = campaign.data;
  const [items, dashboard] = await Promise.all([
    apiGet<ItemView[]>(`/api/v1/content/items?site=${encodeURIComponent(c.siteId)}&status=published&limit=100`),
    apiGet<CampaignDashboard>(`/api/v1/content/campaigns/${encodeURIComponent(id)}/dashboard`),
  ]);
  const t = await getTranslations("content.campaigns");
  const locale = await getLocale();
  const shown = (n: number | null) => (n === null ? t("notEnough") : String(n));
  const row = (label: string, counts: CampaignCounts) => (
    <tr key={label}>
      <th scope="row">
        <bdi>{label}</bdi>
      </th>
      <td>{counts.audience}</td>
      <td>{counts.delivered}</td>
      <td>{shown(counts.viewed)}</td>
      <td>{shown(counts.clicked)}</td>
      <td>{shown(counts.acknowledged)}</td>
    </tr>
  );
  const d = dashboard.ok ? dashboard.data : null;
  const breakdowns = d
    ? [
        { key: "byUnit", caption: t("byUnit"), rows: d.byUnit },
        { key: "byLocation", caption: t("byLocation"), rows: d.byLocation },
      ]
    : [];
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/content/campaigns" },
          { label: c.name, href: campaignHref(c.id) },
        ]}
      />
      <main id="main" className="page content-manage">
        <h1>
          <bdi>{c.name}</bdi>
        </h1>
        <p className="content-meta">
          <Badge tone={c.status === "scheduled" ? "info" : c.status === "completed" ? "success" : "neutral"}>{t(`status.${c.status}`)}</Badge>
        </p>
        <CampaignPlan campaign={c} items={items.ok ? items.data : []} />
        {d ? (
          <section className="content-card" aria-labelledby="campaign-dashboard">
            <h2 id="campaign-dashboard">{t("dashboard")}</h2>
            <p className="field-hint">{t("privacy", { size: d.minimumGroupSize })}</p>
            <table className="content-table">
              <caption>{t("reach")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("stepColumn")}</th>
                  <th scope="col">{t("audience")}</th>
                  <th scope="col">{t("delivered")}</th>
                  <th scope="col">{t("viewed")}</th>
                  <th scope="col">{t("clicked")}</th>
                  <th scope="col">{t("acknowledged")}</th>
                </tr>
              </thead>
              <tbody>
                {d.steps.map((s) => row(t("step", { step: String(s.position) }), s.counts))}
                {row(t("total"), d.total)}
              </tbody>
            </table>
            {breakdowns.map((b) =>
              b.rows.length === 0 ? null : (
                <table key={b.key} className="content-table">
                  <caption>{b.caption}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t("group")}</th>
                      <th scope="col">{t("audience")}</th>
                      <th scope="col">{t("viewed")}</th>
                      <th scope="col">{t("acknowledged")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {b.rows.map((r) => {
                      const label = r.key === null ? t("other") : r.names ? pickName(r.names, locale) : r.key;
                      return (
                        <tr key={r.key ?? "other"}>
                          <th scope="row">
                            <bdi>{label}</bdi>
                          </th>
                          <td>{r.audience}</td>
                          <td>{r.viewed}</td>
                          <td>{r.acknowledged}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              ),
            )}
          </section>
        ) : null}
      </main>
    </>
  );
}
