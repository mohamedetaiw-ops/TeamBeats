import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { LocationRef } from "@/lib/people/types";
import {
  complianceHref,
  nameIn,
  personComplianceHref,
  policyHref,
  type CompliancePeople,
  type ComplianceView,
  type PolicyView,
} from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { RemindButton } from "@/features/policies/RemindButton";
import { ComplianceBreakdown } from "@/features/policies/ComplianceBreakdown";
import { PageHeader } from "@/components/ui/PageHeader";

type Filters = { campaign?: string; unit?: string; direct?: string; location?: string; manager?: string; status?: string; offset?: string };

const statuses = ["overdue", "open", "attested", "left_audience"] as const;
const pageSize = 50;

/**
 * The compliance view of a policy (spec 025 US3): completion of a campaign for the viewer's people by unit, location
 * and manager, each with a link to the people behind the numbers, and the people list with a link to each person's
 * policies. Compliance officers see their units, line managers their reports; the API decides which.
 */
export default async function PolicyCompliancePage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<Filters> }) {
  const { id } = await params;
  const f = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const policy = await apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`);
  if (!policy.ok || !policy.data.rights.compliance) {
    notFound();
  }
  const p = policy.data;
  const viewQuery = new URLSearchParams();
  if (f.campaign) viewQuery.set("campaign", f.campaign);
  if (f.unit) viewQuery.set("unit", f.unit);
  const view = await apiGet<ComplianceView>(`/api/v1/policies/${encodeURIComponent(id)}/compliance${viewQuery.size ? `?${viewQuery}` : ""}`);
  if (!view.ok) {
    notFound();
  }
  const v = view.data;
  const offset = Math.max(0, Number(f.offset) || 0);
  const peopleQuery = new URLSearchParams();
  if (v.campaign) peopleQuery.set("campaign", v.campaign.id);
  if (f.unit) peopleQuery.set("unit", f.unit);
  if (f.direct === "true") peopleQuery.set("direct", "true");
  if (f.location) peopleQuery.set("location", f.location);
  if (f.manager) peopleQuery.set("manager", f.manager);
  if (f.status && (statuses as readonly string[]).includes(f.status)) peopleQuery.set("status", f.status);
  peopleQuery.set("offset", String(offset));
  peopleQuery.set("limit", String(pageSize));
  const [people, locations] = await Promise.all([
    v.campaign ? apiGet<CompliancePeople>(`/api/v1/policies/${encodeURIComponent(id)}/compliance/people?${peopleQuery}`) : null,
    apiGet<LocationRef[]>("/api/v1/people/locations"),
  ]);
  const t = await getTranslations("policies.compliance");
  const locale = await getLocale();
  const format = await getFormatter();
  const name = nameIn(p.names, locale);
  const labels = new Map((locations.ok ? locations.data : []).map((l) => [l.key, nameIn(l.names, locale) || l.label]));
  const href = (patch: Partial<Filters>) => {
    const q = new URLSearchParams();
    const merged: Filters = { campaign: v.campaign?.id, unit: f.unit, ...patch };
    for (const [k, value] of Object.entries(merged)) if (value) q.set(k, value);
    return `${complianceHref(p.id)}${q.size ? `?${q}` : ""}`;
  };
  const percent = (n: number) => format.number(n / 100, { style: "percent", maximumFractionDigits: 1 });
  const parent = v.unitPath.length > 1 ? v.unitPath[v.unitPath.length - 2] : null;

  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(p.id) },
          { label: t("title"), href: complianceHref(p.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} icon="policy" nav={<PoliciesNav current={null} />} />
        <p className="policies-meta">{t(`scope.${v.scope}`)}</p>
        {v.campaigns.length > 1 ? (
          <form className="policies-toolbar" method="get">
            <div className="field">
              <label htmlFor="compliance-campaign">{t("campaign")}</label>
              <select id="compliance-campaign" name="campaign" defaultValue={v.campaign?.id ?? ""}>
                {v.campaigns.map((c) => (
                  <option key={c.id} value={c.id}>
                    {t("campaignOption", { number: c.version, date: format.dateTime(new Date(c.deadline), { dateStyle: "medium" }), status: t(`campaignStatus.${c.status}`) })}
                  </option>
                ))}
              </select>
            </div>
            <button type="submit" className="button button-secondary">
              {t("apply")}
            </button>
          </form>
        ) : null}
        {!v.campaign ? (
          <StateView kind="empty" title={t("noCampaign")} message="" />
        ) : (
          <>
            <section className="policies-card" aria-labelledby="compliance-totals">
              <h2 id="compliance-totals">{t("totals", { number: v.campaign.version })}</h2>
              <p data-testid="compliance-totals">
                {t("summary", {
                  percent: percent(v.totals.percent),
                  attested: v.totals.attested,
                  members: v.totals.members - v.totals.leftAudience,
                  overdue: v.totals.overdue,
                })}
              </p>
              {v.campaign.status === "open" ? <RemindButton policyId={p.id} campaignId={v.campaign.id} /> : null}
            </section>
            {f.unit ? (
              <p className="button-row">
                <Link href={href({ unit: undefined })}>{t("wholeView")}</Link>
                {parent ? <Link href={href({ unit: parent })}>{t("upOneLevel")}</Link> : null}
              </p>
            ) : null}
            <ComplianceBreakdown
              kind="unit"
              rows={v.byUnit.map((g) => ({
                counts: g.counts,
                label:
                g.unitId === null ? (
                  t("noUnit")
                ) : g.direct ? (
                  <bdi>{t("directly", { name: nameIn(g.unitNames, locale) })}</bdi>
                ) : (
                  <Link href={href({ unit: g.unitId })}>
                    <bdi>{nameIn(g.unitNames, locale)}</bdi>
                  </Link>
                ),
                people: href({ unit: g.unitId ?? undefined, direct: g.direct ? "true" : undefined }),
              }))}
            />
            <ComplianceBreakdown
              kind="location"
              rows={v.byLocation.map((g) => ({
                counts: g.counts,
                label: g.location ? <bdi>{labels.get(g.location) ?? g.location}</bdi> : t("noLocation"),
                people: href({ location: g.location ?? "none" }),
              }))}
            />
            <ComplianceBreakdown
              kind="manager"
              rows={v.byManager.map((g) => ({
                counts: g.counts,
                label: g.manager ? <bdi>{g.manager.displayName}</bdi> : t("noManager"),
                people: href({ manager: g.manager?.id ?? "none" }),
              }))}
            />
            <section className="policies-card" aria-labelledby="compliance-people">
              <h2 id="compliance-people">{t("people")}</h2>
              <form className="policies-toolbar" method="get">
                {v.campaign ? <input type="hidden" name="campaign" value={v.campaign.id} /> : null}
                {f.unit ? <input type="hidden" name="unit" value={f.unit} /> : null}
                {f.direct ? <input type="hidden" name="direct" value={f.direct} /> : null}
                {f.location ? <input type="hidden" name="location" value={f.location} /> : null}
                {f.manager ? <input type="hidden" name="manager" value={f.manager} /> : null}
                <div className="field">
                  <label htmlFor="compliance-status">{t("status")}</label>
                  <select id="compliance-status" name="status" defaultValue={f.status ?? ""}>
                    <option value="">{t("allStatuses")}</option>
                    {statuses.map((s) => (
                      <option key={s} value={s}>
                        {t(`memberStatus.${s}`)}
                      </option>
                    ))}
                  </select>
                </div>
                <button type="submit" className="button button-secondary">
                  {t("apply")}
                </button>
              </form>
              {people?.ok ? (
                people.data.items.length === 0 ? (
                  <p className="policies-meta">{t("nobody")}</p>
                ) : (
                  <>
                    <div className="policies-table">
                      <table data-testid="compliance-people">
                        <thead>
                          <tr>
                            <th scope="col">{t("column.person")}</th>
                            <th scope="col">{t("column.status")}</th>
                            <th scope="col">{t("column.when")}</th>
                          </tr>
                        </thead>
                        <tbody>
                          {people.data.items.map((x) => (
                            <tr key={x.person.id}>
                              <th scope="row">
                                <Link href={personComplianceHref(x.person.id)}>
                                  <bdi>{x.person.displayName}</bdi>
                                </Link>
                              </th>
                              <td>{t(`memberStatus.${x.status}`)}</td>
                              <td>
                                {format.dateTime(new Date(x.attestedAt ?? x.leftAt ?? x.dueAt), { dateStyle: "medium" })}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                    <p className="button-row">
                      {offset > 0 ? <Link href={href({ ...f, campaign: v.campaign.id, offset: String(Math.max(0, offset - pageSize)) })}>{t("previous")}</Link> : null}
                      {offset + pageSize < people.data.total ? (
                        <Link href={href({ ...f, campaign: v.campaign.id, offset: String(offset + pageSize) })}>{t("next")}</Link>
                      ) : null}
                      <span className="policies-meta">{t("showing", { from: offset + 1, to: Math.min(offset + pageSize, people.data.total), total: people.data.total })}</span>
                    </p>
                  </>
                )
              ) : (
                <p className="policies-meta">{t("failed")}</p>
              )}
            </section>
          </>
        )}
      </main>
    </>
  );
}
