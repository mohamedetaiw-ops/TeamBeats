import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { contentStatuses, contentTypes, editHref, pickName, variantFor, type ItemView, type SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import "@/features/content/content.css";

/**
 * Items an author or editor manages (spec 009 US1, US3), filtered by site, status and type. The API returns only the
 * items in sites where the person may author, so someone without content rights sees the denied state.
 */
export default async function ContentItemsPage({ searchParams }: { searchParams: Promise<Record<string, string | undefined>> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const query = await searchParams;
  const filter = new URLSearchParams();
  for (const key of ["site", "status", "type"] as const) {
    if (query[key]) filter.set(key, query[key]!);
  }
  filter.set("limit", "100");
  const [items, sites] = await Promise.all([apiGet<ItemView[]>(`/api/v1/content/items?${filter.toString()}`), apiGet<SiteView[]>("/api/v1/content/sites")]);
  const t = await getTranslations("content.items");
  const locale = await getLocale();
  const format = new Intl.DateTimeFormat(locale, { dateStyle: "medium" });
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("listTitle"), href: "/content/items" }]} />
      <main id="main" className="page content-manage">
        <h1>{t("listTitle")}</h1>
        {!items.ok ? (
          <StateView kind="denied" title={t("denied")} message="" />
        ) : (
          <>
            <p className="content-actions">
              <Link className="button button-primary" href="/content/items/new">
                {t("new")}
              </Link>
              <Link href="/content/health">{t("healthLink")}</Link>
            </p>
            <form className="content-filters" method="get" aria-label={t("filters")}>
              <label>
                {t("site")}
                <select name="site" defaultValue={query.site ?? (sites.ok ? sites.data.find((s) => s.isDefault)?.id : undefined)}>
                  {(sites.ok ? sites.data : []).map((s) => (
                    <option key={s.id} value={s.id}>
                      {pickName(s.names, locale)}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                {t("statusLabel")}
                <select name="status" defaultValue={query.status ?? ""}>
                  <option value="">{t("all")}</option>
                  {contentStatuses.map((s) => (
                    <option key={s} value={s}>
                      {t(`status.${s}`)}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                {t("type")}
                <select name="type" defaultValue={query.type ?? ""}>
                  <option value="">{t("all")}</option>
                  {contentTypes.map((c) => (
                    <option key={c} value={c}>
                      {t(`typeValue.${c}`)}
                    </option>
                  ))}
                </select>
              </label>
              <button type="submit" className="button button-secondary">
                {t("apply")}
              </button>
            </form>
            {items.data.length === 0 ? (
              <StateView kind="empty" title={t("none")} message="" />
            ) : (
              <table className="content-table">
                <caption className="visually-hidden">{t("listTitle")}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t("title")}</th>
                    <th scope="col">{t("type")}</th>
                    <th scope="col">{t("statusLabel")}</th>
                    <th scope="col">{t("updated")}</th>
                  </tr>
                </thead>
                <tbody>
                  {items.data.map((item) => {
                    const v = variantFor(item, locale);
                    return (
                      <tr key={item.id}>
                        <td>
                          <Link href={editHref(item.id)}>
                            <bdi>{v?.title ?? item.id}</bdi>
                          </Link>
                        </td>
                        <td>{t(`typeValue.${item.type as (typeof contentTypes)[number]}`)}</td>
                        <td>{t(`status.${item.status as (typeof contentStatuses)[number]}`)}</td>
                        <td>
                          <time dateTime={item.updatedAt}>{format.format(new Date(item.updatedAt))}</time>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </>
        )}
      </main>
    </>
  );
}
