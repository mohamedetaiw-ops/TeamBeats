import Link from "next/link";
import { redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, policyHref, type PolicyListItem, type RegisterRights } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { PageHeader } from "@/components/ui/PageHeader";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { FilterToolbar, ToolbarSearch, ToolbarSelect } from "@/components/ui/FilterToolbar";

const statuses = ["draft", "in_review", "approved", "published", "retired"] as const;
const statusTone: Record<string, BadgeTone> = { draft: "neutral", in_review: "warning", approved: "info", published: "success", retired: "neutral" };

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
  const chosenStatus = statuses.find((s) => s === status);
  const registerHref = (keep: { q?: string; status?: string }) => {
    const next = new URLSearchParams();
    if (keep.q) next.set("q", keep.q);
    if (keep.status) next.set("status", keep.status);
    return next.size > 0 ? `/policies?${next}` : "/policies";
  };
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/policies" }]} />
      <main id="main" className="page policies-page">
        <PageHeader
          title={t("title")}
          description={t("description")}
          icon="policy"
          nav={<PoliciesNav current="register" />}
          actions={
            rights.ok && rights.data.create ? (
              <Link href="/policies/new" className="button button-primary">
                {t("create")}
              </Link>
            ) : null
          }
        />
        <FilterToolbar
          label={t("search")}
          method="get"
          search={<ToolbarSearch label={t("search")} name="q" placeholder={t("search")} defaultValue={q ?? ""} />}
          end={
            <button type="submit" className="button button-secondary">
              {t("apply")}
            </button>
          }
          applied={[
            ...(q?.trim() ? [{ key: "q", label: q.trim(), href: registerHref({ status: chosenStatus }) }] : []),
            ...(chosenStatus ? [{ key: "status", label: `${t("statusFilter")}: ${ts(chosenStatus)}`, href: registerHref({ q: q?.trim() }) }] : []),
          ]}
          clearHref="/policies"
        >
          <ToolbarSelect label={t("statusFilter")} hideLabel name="status" defaultValue={chosenStatus ?? ""}>
            <option value="">{t("allStatuses")}</option>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {ts(s)}
              </option>
            ))}
          </ToolbarSelect>
        </FilterToolbar>
        {!list.ok ? (
          <StateView kind="error" title={t("failed")} message="" />
        ) : list.data.length === 0 ? (
          <StateView kind="empty" title={t("empty")} message="" />
        ) : (
          <div className="policies-table">
            <table data-testid="policy-register" data-stack>
              <caption className="visually-hidden">{t("title")}</caption>
              <thead>
                <tr>
                  <th scope="col" data-priority="low">{t("code")}</th>
                  <th scope="col">{t("name")}</th>
                  <th scope="col">{t("status")}</th>
                  <th scope="col" data-priority="low">{t("version")}</th>
                  <th scope="col" data-priority="low">{t("effective")}</th>
                  <th scope="col">{t("review")}</th>
                  <th scope="col">{t("owner")}</th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((p) => (
                  <tr key={p.id}>
                    <td data-priority="low" data-label={t("code")}>
                      <bdi dir="ltr">{p.code}</bdi>
                    </td>
                    <th scope="row">
                      <Link href={policyHref(p.id)}>
                        <bdi>{nameIn(p.names, locale)}</bdi>
                      </Link>
                    </th>
                    <td data-label={t("status")}>
                      <Badge tone={statusTone[p.status] ?? "neutral"}>{ts(p.status)}</Badge>
                      {p.workingState ? <span className="policies-meta"> · {t("working", { state: ts(p.workingState) })}</span> : null}
                    </td>
                    <td data-priority="low" data-label={t("version")}>{p.currentVersion ?? t("none")}</td>
                    <td data-priority="low" data-label={t("effective")}>{date(p.effectiveFrom)}</td>
                    <td data-label={t("review")}>{date(p.nextReviewAt)}</td>
                    <td data-label={t("owner")}>
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
