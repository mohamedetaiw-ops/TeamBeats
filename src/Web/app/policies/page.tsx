import Link from "next/link";
import { redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, policyHref, type PolicyListItem, type RegisterRights } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";

const statuses = ["draft", "in_review", "approved", "published", "retired"] as const;

/**
 * The policy register (spec 025 section 10): what the person manages, owns, or is published for them, with ID, name,
 * status, version in force, effective date and next review. The API decides which rows each person gets.
 */
export default async function PolicyRegisterPage({ searchParams }: { searchParams: Promise<{ q?: string; status?: string }> }) {
  const { q, status } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const query = new URLSearchParams();
  if (q?.trim()) query.set("q", q.trim());
  if (status && (statuses as readonly string[]).includes(status)) query.set("status", status);
  const [list, rights] = await Promise.all([
    apiGet<PolicyListItem[]>(`/api/v1/policies${query.size > 0 ? `?${query}` : ""}`),
    apiGet<RegisterRights>("/api/v1/policies/rights"),
  ]);
  const t = await getTranslations("policies.register");
  const ts = await getTranslations("policies.status");
  const locale = await getLocale();
  const format = await getFormatter();
  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium" }) : t("none"));
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/policies" }]} />
      <main id="main" className="page policies-page">
        <h1>{t("title")}</h1>
        <PoliciesNav current="register" />
        {rights.ok && rights.data.create ? (
          <p>
            <Link href="/policies/new" className="button button-primary">
              {t("create")}
            </Link>
          </p>
        ) : null}
        <form className="policies-toolbar" method="get" role="search" aria-label={t("search")}>
          <div className="field">
            <label htmlFor="policy-q">{t("search")}</label>
            <input id="policy-q" name="q" type="search" defaultValue={q ?? ""} dir="auto" />
          </div>
          <div className="field">
            <label htmlFor="policy-status">{t("statusFilter")}</label>
            <select id="policy-status" name="status" defaultValue={status ?? ""}>
              <option value="">{t("allStatuses")}</option>
              {statuses.map((s) => (
                <option key={s} value={s}>
                  {ts(s)}
                </option>
              ))}
            </select>
          </div>
          <button type="submit" className="button button-secondary">
            {t("apply")}
          </button>
        </form>
        {!list.ok ? (
          <StateView kind="error" title={t("failed")} message="" />
        ) : list.data.length === 0 ? (
          <StateView kind="empty" title={t("empty")} message="" />
        ) : (
          <div className="policies-table">
            <table data-testid="policy-register">
              <caption className="visually-hidden">{t("title")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("code")}</th>
                  <th scope="col">{t("name")}</th>
                  <th scope="col">{t("status")}</th>
                  <th scope="col">{t("version")}</th>
                  <th scope="col">{t("effective")}</th>
                  <th scope="col">{t("review")}</th>
                  <th scope="col">{t("owner")}</th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((p) => (
                  <tr key={p.id}>
                    <td>
                      <bdi dir="ltr">{p.code}</bdi>
                    </td>
                    <td>
                      <Link href={policyHref(p.id)}>
                        <bdi>{nameIn(p.names, locale)}</bdi>
                      </Link>
                    </td>
                    <td>
                      {ts(p.status)}
                      {p.workingState ? <span className="policies-meta"> · {t("working", { state: ts(p.workingState) })}</span> : null}
                    </td>
                    <td>{p.currentVersion ?? t("none")}</td>
                    <td>{date(p.effectiveFrom)}</td>
                    <td>{date(p.nextReviewAt)}</td>
                    <td>
                      <bdi>{p.owner.displayName}</bdi>
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
