import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { requestStatuses, type RequestSummary } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { renderTime } from "@/lib/format";
import { StateView } from "@/components/ui/StateView";
import { ServicesNav } from "@/features/services/ServicesNav";
import { WorkItemList } from "@/features/services/WorkItemList";
import "@/features/services/services.css";
import { PageHeader } from "@/components/ui/PageHeader";

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
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("team.title"), href: "/requests/team" }]} />
      <main id="main" className="page">
        <PageHeader title={t("team.title")} icon="services" nav={<ServicesNav current="team" />} />
        <nav aria-label={t("requests.filter")} className="ui-chips">
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
          <WorkItemList items={list.data} now={renderTime()} person="requester" testId="team-requests" />
        )}
      </main>
    </>
  );
}
