import Link from "next/link";
import { redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, policyHref, type MyPolicyItem } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";

/**
 * My policies (spec 025 section 10): the policies that apply to the person, those that need an attestation first with
 * their due date, then the ones accepted with the version and date.
 */
export default async function MyPoliciesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const list = await apiGet<MyPolicyItem[]>("/api/v1/me/policies");
  const t = await getTranslations("policies.my");
  const locale = await getLocale();
  const format = await getFormatter();
  const date = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "long" });
  const items = list.ok ? list.data : [];
  const due = items.filter((i) => i.due);
  const rest = items.filter((i) => !i.due);
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/policies/my" }]} />
      <main id="main" className="page policies-page">
        <h1>{t("title")}</h1>
        <PoliciesNav current="my" />
        {!list.ok ? (
          <StateView kind="error" title={t("failed")} message="" />
        ) : items.length === 0 ? (
          <StateView kind="empty" title={t("empty")} message="" />
        ) : (
          <>
            <section aria-labelledby="my-due" className="policies-page">
              <h2 id="my-due">{t("dueTitle", { count: due.length })}</h2>
              {due.length === 0 ? (
                <p className="policies-meta">{t("nothingDue")}</p>
              ) : (
                <ul className="policies-list" data-testid="my-policies-due">
                  {due.map((i) => (
                    <li key={i.policy.id} className="policies-card">
                      <Link href={`${policyHref(i.policy.id)}?attest=1`}>
                        <bdi>{nameIn(i.policy.names, locale)}</bdi>
                      </Link>
                      <p className={i.due!.overdue ? "field-error" : "policies-meta"}>
                        {t(i.due!.overdue ? "overdue" : "due", { number: i.due!.version, date: date(i.due!.dueAt) })}
                      </p>
                    </li>
                  ))}
                </ul>
              )}
            </section>
            <section aria-labelledby="my-all" className="policies-page">
              <h2 id="my-all">{t("allTitle")}</h2>
              <ul className="policies-list" data-testid="my-policies">
                {rest.map((i) => (
                  <li key={i.policy.id} className="policies-card">
                    <Link href={policyHref(i.policy.id)}>
                      <bdi>{nameIn(i.policy.names, locale)}</bdi>
                    </Link>
                    <p className="policies-meta">
                      {i.attestedVersion !== null && i.attestedAt
                        ? t(i.attestedVersion === i.currentVersion ? "accepted" : "acceptedEarlier", { number: i.attestedVersion, date: date(i.attestedAt) })
                        : t("inForce", { number: i.currentVersion ?? 0 })}
                    </p>
                  </li>
                ))}
              </ul>
            </section>
          </>
        )}
      </main>
    </>
  );
}
