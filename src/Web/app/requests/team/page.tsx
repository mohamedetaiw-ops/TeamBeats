import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pick, requestStatuses, type RequestSummary } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { ServicesNav } from "@/features/services/ServicesNav";
import "@/features/services/services.css";

/**
 * Team requests (spec 016 FR-005, US2 scenario 4): requests for the signed-in person's direct and indirect reports by
 * the reporting line, newest first, filtered by status. The API decides who is in the team.
 */
export default async function TeamRequestsPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  const { status } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const filter = requestStatuses.find((s) => s === status) ?? null;
  const list = await apiGet<RequestSummary[]>(`/api/v1/requests/team${filter ? `?status=${filter}` : ""}`);
  if (!list.ok) {
    notFound();
  }
  const t = await getTranslations("services");
  const locale = await getLocale();
  const format = await getFormatter();
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("team.title"), href: "/requests/team" }]} />
      <main id="main" className="page">
        <h1>{t("team.title")}</h1>
        <ServicesNav current="team" />
        <nav aria-label={t("requests.filter")} className="services-nav">
          <ul>
            <li>
              <Link href="/requests/team" aria-current={filter === null ? "page" : undefined}>
                {t("requests.all")}
              </Link>
            </li>
            {requestStatuses.map((s) => (
              <li key={s}>
                <Link href={`/requests/team?status=${s}`} aria-current={filter === s ? "page" : undefined}>
                  {t(`status.${s}`)}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        {list.data.length === 0 ? (
          <StateView kind="empty" title={t("team.empty")} message={t("team.emptyHint")} />
        ) : (
          <ul className="services-list" data-testid="team-requests">
            {list.data.map((r) => (
              <li key={r.id} className="services-item">
                <Link href={`/requests/${r.id}`}>
                  <bdi>{pick(r.serviceNames, locale)}</bdi>
                </Link>
                <span className="services-row">
                  <bdi dir="ltr">{r.number}</bdi>
                  <span className="services-status">{t(`status.${r.status}`)}</span>
                  {r.currentStep ? <bdi>{pick(r.currentStep, locale)}</bdi> : null}
                </span>
                <span className="services-meta">
                  {t("team.for", { name: (r.onBehalfOf ?? r.requester).displayName })}
                  {r.submittedAt ? t("requests.submittedAt", { when: format.dateTime(new Date(r.submittedAt), { dateStyle: "medium" }) }) : null}
                </span>
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
