import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { FrontendErrorsView } from "@/lib/analytics/types";
import { StateView } from "@/components/ui/StateView";
import "@/features/analytics/analytics.css";

/**
 * Frontend errors for support (spec 021 US9 scenario 6, FR-020): failures of the last days by release, kind, component
 * version and area, with the latest correlation IDs. Reports never carry a message or payload.
 */
export default async function FrontendErrorsPage({ searchParams }: { searchParams: Promise<{ release?: string }> }) {
  const { release } = await searchParams;
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "analytics")) notFound();
  const query = release ? `?release=${encodeURIComponent(release)}` : "";
  const errors = await apiGet<FrontendErrorsView>(`/api/v1/analytics/errors${query}`);
  if (!errors.ok) notFound();
  const v = errors.data;
  const t = await getTranslations("analytics.errors");
  const format = await getFormatter();
  const when = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });

  return (
    <div className="analytics-item" data-testid="frontend-errors">
      <h1>{t("title")}</h1>
      <p className="analytics-meta">{t("intro", { days: v.days })}</p>
      <p>
        <Link href="/admin/analytics">{t("back")}</Link>
      </p>
      {v.releases.length > 0 ? (
        <form className="analytics-remind" method="get">
          <div className="field">
            <label htmlFor="release">{t("release")}</label>
            <select id="release" name="release" defaultValue={v.release ?? ""}>
              <option value="">{t("allReleases")}</option>
              {v.releases.map((r) => (
                <option key={r.release} value={r.release}>
                  {t("releaseOption", { release: r.release, count: r.count })}
                </option>
              ))}
            </select>
          </div>
          <button type="submit" className="button button-secondary">
            {t("filter")}
          </button>
        </form>
      ) : null}
      {v.groups.length === 0 ? (
        <StateView kind="empty" title={t("none")} message="" />
      ) : (
        <div className="analytics-table">
          <table data-testid="frontend-error-groups">
            <thead>
              <tr>
                <th scope="col">{t("column.release")}</th>
                <th scope="col">{t("column.kind")}</th>
                <th scope="col">{t("column.component")}</th>
                <th scope="col">{t("column.area")}</th>
                <th scope="col">{t("column.count")}</th>
                <th scope="col">{t("column.last")}</th>
                <th scope="col">{t("column.correlation")}</th>
              </tr>
            </thead>
            <tbody>
              {v.groups.map((g) => (
                <tr key={`${g.release}|${g.kind}|${g.component ?? ""}|${g.area ?? ""}`}>
                  <th scope="row">
                    <bdi dir="ltr">{g.release}</bdi>
                  </th>
                  <td>{t(`kind.${g.kind}`)}</td>
                  <td>{g.component ? <bdi dir="ltr">{g.component}</bdi> : t("noComponent")}</td>
                  <td>{g.area ? <bdi dir="ltr">{g.area}</bdi> : ""}</td>
                  <td data-number>{format.number(g.count)}</td>
                  <td>{when(g.lastAt)}</td>
                  <td>
                    {g.correlationIds.map((id) => (
                      <code key={id} dir="ltr" className="analytics-id">
                        {id}
                      </code>
                    ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
