import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, personComplianceHref, policyHref, type PersonCompliance } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";

/** One person in the compliance view (spec 025 US3 scenario 2): every policy that applies to them, what they owe and the last version accepted. */
export default async function PersonCompliancePage({ params }: { params: Promise<{ userId: string }> }) {
  const { userId } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const person = await apiGet<PersonCompliance>(`/api/v1/policies/compliance/people/${encodeURIComponent(userId)}`);
  if (!person.ok) {
    notFound();
  }
  const t = await getTranslations("policies.person");
  const locale = await getLocale();
  const format = await getFormatter();
  const date = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "long" });
  const p = person.data;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: p.person.displayName, href: personComplianceHref(p.person.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <h1>
          <bdi>{t("heading", { name: p.person.displayName })}</bdi>
        </h1>
        <PoliciesNav current={null} />
        {p.policies.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : (
          <div className="policies-table">
            <table data-testid="person-policies">
              <thead>
                <tr>
                  <th scope="col">{t("policy")}</th>
                  <th scope="col">{t("inForce")}</th>
                  <th scope="col">{t("state")}</th>
                </tr>
              </thead>
              <tbody>
                {p.policies.map((i) => (
                  <tr key={i.policy.id}>
                    <th scope="row">
                      <Link href={policyHref(i.policy.id)}>
                        <bdi>{nameIn(i.policy.names, locale)}</bdi>
                      </Link>
                    </th>
                    <td>{i.currentVersion ?? ""}</td>
                    <td>
                      {i.due
                        ? t(i.due.overdue ? "overdue" : "due", { number: i.due.version, date: date(i.due.dueAt) })
                        : i.attestedVersion !== null && i.attestedAt
                          ? t("accepted", { number: i.attestedVersion, date: date(i.attestedAt) })
                          : t("notAsked")}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </main>
    </>
  );
}
