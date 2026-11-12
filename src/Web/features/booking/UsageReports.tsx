"use client";

import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { bookingApi, call, lastWeeksIso, pick, reportGroups, type ReportGroup, type ReportSite, type UsageReport, type UsageRow } from "@/lib/booking";
import "./booking.css";

/**
 * How space is used (spec 031 US6, FR-014): utilization and no-show rates for a site by resource, zone, site or week.
 * Only counts and hours are shown, never who booked. The API checks the report right in the site's scope and the range.
 */
export function UsageReports({ sites }: { sites: ReportSite[] }) {
  const t = useTranslations("booking.reports");
  const locale = useLocale();
  const format = useFormatter();
  const initial = lastWeeksIso();
  const [site, setSite] = useState(sites[0]?.id ?? "");
  const [from, setFrom] = useState(initial.from);
  const [to, setTo] = useState(initial.to);
  const [group, setGroup] = useState<ReportGroup>("resource");
  const [report, setReport] = useState<UsageReport | "loading" | string[]>("loading");

  useEffect(() => {
    if (!site || !from || !to) return;
    let live = true;
    const query = new URLSearchParams({ site, from, to, groupBy: group });
    void call<UsageReport>(() => fetch(`${bookingApi}/reports/utilization?${query}`, { cache: "no-store" }), t("failed")).then((r) => {
      if (live) setReport(r.ok ? r.value : r.messages);
    });
    return () => {
      live = false;
    };
  }, [site, from, to, group, t]);

  if (sites.length === 0) return <StateView kind="empty" title={t("noSitesTitle")} message={t("noSites")} icon="building" />;

  const percent = (value: number | null) => (value === null ? t("none") : format.number(value, { style: "percent", maximumFractionDigits: 1 }));
  const hours = (value: number) => format.number(value, { maximumFractionDigits: 1 });
  const label = (row: UsageRow) => {
    if (group === "week" && row.weekStart) {
      return t("weekOf", { date: format.dateTime(new Date(`${row.weekStart}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) });
    }
    if (group === "zone" && row.key === "none") return t("noZone");
    return pick(row.names, locale);
  };

  return (
    <div className="booking">
      <section className="booking-section" aria-labelledby="report-filters">
        <h2 id="report-filters">{t("filters")}</h2>
        <div className="booking-row">
          <SelectField label={t("site")} name="report-site" value={site} onChange={(e) => { setReport("loading"); setSite(e.target.value); }}
            options={sites.map((s) => ({ value: s.id, label: pick(s.names, locale) }))} />
          <FormField label={t("from")} name="report-from" type="date" value={from} max={to} onChange={(e) => { setReport("loading"); setFrom(e.target.value); }} required />
          <FormField label={t("to")} name="report-to" type="date" value={to} min={from} onChange={(e) => { setReport("loading"); setTo(e.target.value); }} required />
          <SelectField label={t("groupBy")} name="report-group" value={group} onChange={(e) => { setReport("loading"); setGroup(e.target.value as ReportGroup); }}
            options={reportGroups.map((g) => ({ value: g, label: t(`group.${g}`) }))} />
        </div>
        <p className="booking-meta">{t("rangeHint")}</p>
      </section>

      {report === "loading" ? (
        <StateView kind="loading" minBlockSize="8rem" />
      ) : Array.isArray(report) ? (
        <StateView kind="error" title={t("failed")} message={report.join(" ")} />
      ) : (
        <>
          <section className="booking-section" aria-labelledby="report-total">
            <h2 id="report-total">{t("total", { site: pick(report.siteNames, locale) })}</h2>
            <div className="booking-row" role="group" aria-label={t("totalLabel")}>
              <Badge tone="info">{t("utilizationOf", { value: percent(report.total.utilization) })}</Badge>
              <Badge tone={report.total.noShowRate !== null && report.total.noShowRate >= 0.2 ? "warning" : "neutral"}>
                {t("noShowRateOf", { value: percent(report.total.noShowRate) })}
              </Badge>
              <Badge>{t("bookingsOf", { count: report.total.bookings })}</Badge>
            </div>
            <p className="booking-meta">{t("explain")}</p>
          </section>

          <section className="booking-section" aria-labelledby="report-rows">
            <h2 id="report-rows">{t(`group.${report.groupBy}`)}</h2>
            {report.rows.length === 0 ? (
              <StateView kind="empty" title={t("noRowsTitle")} message={t("noRows")} icon="trend-up" />
            ) : (
              <div className="booking-table">
                <table>
                  <caption className="booking-meta">{t("caption")}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t(`group.${report.groupBy}`)}</th>
                      <th scope="col">{t("column.resources")}</th>
                      <th scope="col">{t("column.bookings")}</th>
                      <th scope="col">{t("column.checkedIn")}</th>
                      <th scope="col">{t("column.noShows")}</th>
                      <th scope="col">{t("column.cancelled")}</th>
                      <th scope="col">{t("column.usedHours")}</th>
                      <th scope="col">{t("column.availableHours")}</th>
                      <th scope="col">{t("column.utilization")}</th>
                      <th scope="col">{t("column.noShowRate")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {report.rows.map((row) => (
                      <tr key={row.key}>
                        <th scope="row">
                          <bdi>{label(row)}</bdi>
                        </th>
                        <td data-number>{format.number(row.resources)}</td>
                        <td data-number>{format.number(row.bookings)}</td>
                        <td data-number>{format.number(row.checkedIn)}</td>
                        <td data-number>{format.number(row.noShows)}</td>
                        <td data-number>{format.number(row.cancelled)}</td>
                        <td data-number>{hours(row.usedHours)}</td>
                        <td data-number>{hours(row.availableHours)}</td>
                        <td data-number>{percent(row.utilization)}</td>
                        <td data-number>{percent(row.noShowRate)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </div>
  );
}
