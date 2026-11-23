import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { complianceHref, nameIn, personComplianceHref, type RegisterRights, type TeamDashboard } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { Badge } from "@/components/ui/Badge";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import "@/features/policies/policies.css";

/**
 * The manager's team dashboard (spec 025 R2 manager dashboards): every open campaign with some of the viewer's direct
 * and indirect reports, the counts, who is paused or exempt, and who still owes it. The API decides who is in the team
 * and refuses people without reports.
 */
export default async function PolicyTeamPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [team, rights] = await Promise.all([apiGet<TeamDashboard>("/api/v1/policies/team"), apiGet<RegisterRights>("/api/v1/policies/rights")]);
  if (!team.ok) {
    notFound();
  }
  const t = await getTranslations("policies.team");
  const locale = await getLocale();
  const format = await getFormatter();
  const day = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" });
  const r = rights.ok ? rights.data : null;
  const d = team.data;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: t("title"), href: "/policies/team" },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader
          title={t("title")}
          description={t("description")}
          icon="policy"
          nav={<PoliciesNav current="team" team exemptions={r?.exemptions === true} evidence={r?.evidence === true} />}
        />
        <p className="policies-meta" data-testid="team-size">
          {t("reports", { reports: d.reports, direct: d.direct })}
        </p>
        {d.policies.length === 0 ? (
          <StateView kind="empty" title={t("noneTitle")} message={t("none")} />
        ) : (
          d.policies.map((p) => {
            const name = nameIn(p.policy.names, locale);
            return (
              <section key={p.campaignId} className="policies-card" aria-labelledby={`team-${p.campaignId}`} data-testid="team-policy">
                <h2 id={`team-${p.campaignId}`}>
                  <bdi>{name}</bdi>
                </h2>
                <p className="policies-meta">{t("version", { number: p.version, date: day(p.deadline) })}</p>
                <p>
                  {t("summary", { attested: p.counts.attested, members: p.counts.members - p.counts.leftAudience, open: p.counts.open, overdue: p.counts.overdue })}
                  {p.paused > 0 ? (
                    <>
                      {" "}
                      <Badge tone="neutral">{t("paused", { count: p.paused })}</Badge>
                    </>
                  ) : null}
                  {p.exempt > 0 ? (
                    <>
                      {" "}
                      <Badge tone="info">{t("exempt", { count: p.exempt })}</Badge>
                    </>
                  ) : null}
                </p>
                {p.owing.length === 0 ? (
                  <p className="policies-meta">{t("allDone")}</p>
                ) : (
                  <div className="policies-table">
                    <table aria-label={t("owing")}>
                      <thead>
                        <tr>
                          <th scope="col">{t("column.person")}</th>
                          <th scope="col">{t("column.status")}</th>
                          <th scope="col">{t("column.due")}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {p.owing.map((o) => (
                          <tr key={o.person.id}>
                            <th scope="row">
                              <Link href={personComplianceHref(o.person.id)}>
                                <bdi>{o.person.displayName}</bdi>
                              </Link>
                              {o.direct ? null : <span className="policies-meta"> {t("indirect")}</span>}
                            </th>
                            <td>
                              {o.status === "overdue" ? <Badge tone="danger">{t("memberStatus.overdue")}</Badge> : t("memberStatus.open")}
                              {o.pausedUntil ? (
                                <>
                                  {" "}
                                  <Badge tone="neutral">{t("pausedUntil", { date: day(o.pausedUntil) })}</Badge>
                                </>
                              ) : null}
                              {o.exemptUntil ? (
                                <>
                                  {" "}
                                  <Badge tone="info">{t("exemptUntil", { date: day(o.exemptUntil) })}</Badge>
                                </>
                              ) : null}
                            </td>
                            <td>{day(o.dueAt)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                <p className="button-row">
                  <Link href={`${complianceHref(p.policy.id)}?campaign=${encodeURIComponent(p.campaignId)}`}>{t("openCompliance")}</Link>
                </p>
              </section>
            );
          })
        )}
      </main>
    </>
  );
}
