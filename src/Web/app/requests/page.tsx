import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pick, requestStatuses, type RequestSummary } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { renderTime } from "@/lib/format";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { ServicesNav } from "@/features/services/ServicesNav";
import { WorkItemList } from "@/features/services/WorkItemList";
import "@/features/services/services.css";
import { PageHeader } from "@/components/ui/PageHeader";

const views = ["all", "active", "completed"] as const;
type View = (typeof views)[number];

/**
 * My requests (spec 016 FR-005; spec 036 DLS-061): requests filed by or for the signed-in person, newest first. The
 * view chips split open requests from closed ones by the closing time the API gives; the filter bar narrows by status
 * (sent to the API) and by words in the service name or number.
 */
export default async function MyRequestsPage({ searchParams }: { searchParams: Promise<{ status?: string; view?: string; q?: string }> }) {
  const params = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const filter = requestStatuses.find((s) => s === params.status) ?? null;
  const view: View = views.find((v) => v === params.view) ?? "all";
  const query = (params.q ?? "").trim();
  const list = await apiGet<RequestSummary[]>(`/api/v1/requests${filter ? `?status=${filter}` : ""}`);
  if (!list.ok) {
    notFound();
  }
  const t = await getTranslations("services");
  const locale = me.data.locale;
  const needle = query.toLocaleLowerCase(locale);
  const shown = list.data.filter(
    (r) =>
      (view === "all" || (view === "active" ? r.closedAt === null : r.closedAt !== null)) &&
      (!needle || r.number.toLocaleLowerCase(locale).includes(needle) || pick(r.serviceNames, locale).toLocaleLowerCase(locale).includes(needle)),
  );
  const href = (next: View) => {
    const q = new URLSearchParams();
    if (next !== "all") q.set("view", next);
    if (filter) q.set("status", filter);
    if (query) q.set("q", query);
    const s = q.toString();
    return s ? `/requests?${s}` : "/requests";
  };
  const narrowed = filter !== null || view !== "all" || query !== "";
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("requests.title"), href: "/requests" }]} />
      <main id="main" className="page">
        <PageHeader title={t("requests.title")} icon="services" nav={<ServicesNav current="requests" />} />
        <div className="work-items-bar">
          <nav aria-label={t("requests.view")} className="ui-chips">
            <ul>
              {views.map((v) => (
                <li key={v}>
                  <Link href={href(v)} aria-current={view === v ? "page" : undefined}>
                    {t(`requests.views.${v}`)}
                  </Link>
                </li>
              ))}
            </ul>
          </nav>
          <form className="ui-filter-bar work-items-filters" action="/requests" method="get" role="search">
            {view !== "all" ? <input type="hidden" name="view" value={view} /> : null}
            <label className="field">
              <span className="visually-hidden">{t("requests.search")}</span>
              <input type="search" name="q" defaultValue={query} placeholder={t("requests.search")} />
            </label>
            <label className="field">
              <span className="visually-hidden">{t("requests.filter")}</span>
              <select name="status" defaultValue={filter ?? ""}>
                <option value="">{t("requests.anyStatus")}</option>
                {requestStatuses.map((s) => (
                  <option key={s} value={s}>
                    {t(`status.${s}`)}
                  </option>
                ))}
              </select>
            </label>
            <button type="submit" className="button button-secondary">
              <Icon name="filter" size={16} />
              {t("requests.apply")}
            </button>
          </form>
        </div>
        {shown.length === 0 ? (
          narrowed ? (
            <StateView
              kind="empty"
              icon="search"
              title={t("requests.noMatch")}
              message={t("requests.noMatchHint")}
              action={
                <Link className="button button-secondary" href="/requests">
                  {t("requests.clear")}
                </Link>
              }
            />
          ) : (
            <StateView
              kind="empty"
              title={t("requests.empty")}
              message={t("requests.emptyHint")}
              action={
                <Link className="button button-primary" href="/services">
                  {t("requests.browse")}
                </Link>
              }
            />
          )
        ) : (
          <WorkItemList items={shown} now={renderTime()} person="onBehalfOf" testId="my-requests" />
        )}
      </main>
    </>
  );
}
