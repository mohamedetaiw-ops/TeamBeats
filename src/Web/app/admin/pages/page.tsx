import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getCatalogue, listPages, listUnits } from "@/lib/experience/client";
import { pagePath, unitName } from "@/lib/experience/builder-types";
import { StateView } from "@/components/ui/StateView";
import "@/features/builder/builder.css";

/**
 * Pages (spec 032 US1): the pages the editor may change, newest change first, with their template, scope and release.
 * The section appears only when the API lists it, and the list holds only what the API returns for this editor.
 */
export default async function PagesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const [pages, catalogue, units] = await Promise.all([listPages(), getCatalogue(), listUnits()]);
  const t = await getTranslations("builder");
  const locale = await getLocale();
  if (!pages.ok || !catalogue.ok) {
    return (
      <>
        <h1>{t("pages.title")}</h1>
        <StateView kind={pages.ok || pages.status !== 403 ? "error" : "denied"} title={t("pages.unavailable")} message="" />
      </>
    );
  }
  const templates = new Map(catalogue.data.templates.map((x) => [x.id, x.name]));
  const unitNames = new Map(units.ok ? units.data.map((u) => [u.id, unitName(u, locale)]) : []);
  const when = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });

  return (
    <>
      <div className="builder-heading">
        <h1>{t("pages.title")}</h1>
        <Link className="button button-primary" href="/admin/pages/new">
          {t("pages.create")}
        </Link>
      </div>
      <p className="notice">{t("pages.intro")}</p>
      {pages.data.length === 0 ? (
        <StateView kind="empty" title={t("pages.empty")} message={t("pages.emptyMessage")} />
      ) : (
        <div className="builder-table-wrap">
          <table className="builder-table" data-testid="pages-table">
            <thead>
              <tr>
                <th scope="col">{t("pages.address")}</th>
                <th scope="col">{t("pages.template")}</th>
                <th scope="col">{t("pages.scope")}</th>
                <th scope="col">{t("pages.status")}</th>
                <th scope="col">{t("pages.changed")}</th>
              </tr>
            </thead>
            <tbody>
              {pages.data.map((p) => (
                <tr key={p.id}>
                  <th scope="row">
                    <Link href={`/admin/pages/${p.id}`}>
                      <bdi dir="ltr">{pagePath(p.slug)}</bdi>
                    </Link>
                  </th>
                  <td>{templates.get(p.templateId) ?? p.templateId}</td>
                  <td>{p.orgUnitId ? (unitNames.get(p.orgUnitId) ?? t("pages.unit")) : t("pages.wholeTenant")}</td>
                  <td>{p.currentRelease === null ? t("pages.draftOnly") : t("pages.published", { release: p.currentRelease })}</td>
                  <td>
                    <time dateTime={p.draftUpdatedAt}>{when.format(new Date(p.draftUpdatedAt))}</time>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
