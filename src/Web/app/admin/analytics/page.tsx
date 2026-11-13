import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { noUnitGroup, otherGroup, type AdoptionView } from "@/lib/analytics/types";
import { StateView } from "@/components/ui/StateView";
import { AdoptionCharts } from "@/features/analytics/AdoptionCharts";
import "@/features/analytics/analytics.css";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/**
 * Adoption basics for tenant administrators (spec 021 T007): weekly active people, active people by unit, personal
 * layout use and pages per template version. Weekly active people and units are charts with table alternatives (spec 021 FR-025). The API applies the minimum group size; the page exists only
 * when the API lists the section.
 */
export default async function AdoptionPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "analytics")) notFound();
  const adoption = await apiGet<AdoptionView>("/api/v1/analytics/adoption");
  if (!adoption.ok) notFound();
  const a = adoption.data;
  const t = await getTranslations("analytics.adoption");
  const locale = await getLocale();
  const format = await getFormatter();
  const week = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const count = (n: number | null) => (n === null ? t("hidden", { size: a.minGroupSize }) : format.number(n));
  const unit = (group: string, names: Record<string, string>) =>
    group === otherGroup ? t("other", { size: a.minGroupSize }) : group === noUnitGroup ? t("noUnit") : pick(names, locale);

  return (
    <div className="analytics-item" data-testid="adoption">
      <h1>{t("title")}</h1>
      <p className="analytics-meta">{t("intro", { size: a.minGroupSize })}</p>
      <p>
        <Link href="/admin/analytics/errors">{t("errorsLink")}</Link>
      </p>

      {a.weeks.length === 0 && a.units.length === 0 ? (
        <section className="analytics-card">
          <StateView kind="empty" title={t("noData")} message="" />
        </section>
      ) : (
        <AdoptionCharts
          weeks={a.weeks.map((w) => ({ label: week(w.week), value: w.activeUsers }))}
          units={a.units.map((u) => ({ key: u.group, label: unit(u.group, u.names), value: u.activeUsers }))}
          unitsTitle={a.unitsWeek ? t("unitsOf", { week: week(a.unitsWeek) }) : t("units")}
          minGroupSize={a.minGroupSize}
        />
      )}

      <section aria-labelledby="adoption-layouts" className="analytics-card">
        <h2 id="adoption-layouts">{t("personalization")}</h2>
        {a.personalization.length === 0 ? (
          <p className="analytics-meta">{t("noData")}</p>
        ) : (
          <div className="analytics-table">
            <table data-testid="adoption-personalization">
              <thead>
                <tr>
                  <th scope="col">{t("column.week")}</th>
                  <th scope="col">{t("column.people")}</th>
                  <th scope="col">{t("column.customized")}</th>
                  <th scope="col">{t("column.reset")}</th>
                </tr>
              </thead>
              <tbody>
                {a.personalization.map((w) => (
                  <tr key={w.week}>
                    <th scope="row">{week(w.week)}</th>
                    <td data-number>{count(w.people)}</td>
                    <td data-number>{count(w.customized)}</td>
                    <td data-number>{count(w.reset)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section aria-labelledby="adoption-templates" className="analytics-card">
        <h2 id="adoption-templates">{t("templates")}</h2>
        {a.templates.length === 0 ? (
          <p className="analytics-meta">{t("noPages")}</p>
        ) : (
          <div className="analytics-table">
            <table data-testid="adoption-templates">
              <thead>
                <tr>
                  <th scope="col">{t("column.template")}</th>
                  <th scope="col">{t("column.version")}</th>
                  <th scope="col">{t("column.pages")}</th>
                  <th scope="col">{t("column.published")}</th>
                </tr>
              </thead>
              <tbody>
                {a.templates.map((x) => (
                  <tr key={`${x.templateId}@${x.templateVersion}`}>
                    <th scope="row">
                      <bdi dir="ltr">{x.templateId}</bdi>
                    </th>
                    <td>
                      <bdi dir="ltr">{x.templateVersion}</bdi>
                    </td>
                    <td data-number>{format.number(x.pages)}</td>
                    <td data-number>{format.number(x.published)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
