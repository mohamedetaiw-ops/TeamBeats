import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { controlPoliciesHref, nameIn, policyHref, type ControlPoliciesView } from "@/lib/policies/types";
import { Badge } from "@/components/ui/Badge";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import "@/features/policies/policies.css";

/** A control of the library with the policies mapped to it and their status (spec 025 US8 scenario 3), as far as the reader may see them. */
export default async function ControlPoliciesPage({ searchParams }: { searchParams: Promise<{ framework?: string; code?: string }> }) {
  const { framework, code } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!framework || !code) {
    notFound();
  }
  const view = await apiGet<ControlPoliciesView>(`/api/v1/policies/by-control?framework=${encodeURIComponent(framework)}&code=${encodeURIComponent(code)}`);
  if (!view.ok) {
    notFound();
  }
  const v = view.data;
  const t = await getTranslations("policies.controls");
  const locale = await getLocale();
  const format = await getFormatter();
  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium" }) : t("never"));
  const title = t("codeLabel", { code: v.control.code, title: v.control.titles ? nameIn(v.control.titles, locale) : "" });
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: title, href: controlPoliciesHref(v.control.framework, v.control.code) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader
          title={title}
          description={v.control.frameworkNames ? t("frameworkLabel", { name: nameIn(v.control.frameworkNames, locale), version: v.control.frameworkVersion ?? "" }) : v.control.framework}
          icon="policy"
          nav={<PoliciesNav current={null} />}
        />
        {v.policies.length === 0 ? (
          <StateView kind="empty" title={t("noPolicies")} message="" />
        ) : (
          <section className="policies-card" aria-label={t("policies")}>
            <div className="policies-table">
              <table data-testid="control-policies" data-stack>
                <thead>
                  <tr>
                    <th scope="col">{t("policy")}</th>
                    <th scope="col">{t("status")}</th>
                    <th scope="col">{t("inForce")}</th>
                    <th scope="col">{t("nextReview")}</th>
                  </tr>
                </thead>
                <tbody>
                  {v.policies.map((p) => (
                    <tr key={p.policy.id}>
                      <th scope="row" data-label={t("policy")}>
                        <Link href={policyHref(p.policy.id)}>
                          <bdi>{t("policyName", { code: p.policy.code, name: nameIn(p.policy.names, locale) })}</bdi>
                        </Link>
                      </th>
                      <td data-label={t("status")}>
                        {t(`statuses.${p.policy.status}`)}
                        {p.reviewState === "overdue" ? (
                          <>
                            {" "}
                            <Badge tone="warning">{t("reviewOverdue")}</Badge>
                          </>
                        ) : null}
                      </td>
                      <td data-label={t("inForce")}>{p.currentVersion ? t("version", { number: p.currentVersion, date: date(p.effectiveFrom) }) : t("notInForce")}</td>
                      <td data-label={t("nextReview")}>{date(p.nextReviewAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        )}
      </main>
    </>
  );
}
