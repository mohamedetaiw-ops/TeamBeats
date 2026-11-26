"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { scopeHref, type ScopeDashboardView, type ScopeRef } from "@/lib/analytics/types";
import { DataTable } from "@/components/ui/DataTable";
import { Alert } from "@/components/ui/Alert";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** The units and locations the person may open (spec 021 US6). */
export function ScopeList({ scopes, caption, locationNames }: { scopes: readonly ScopeRef[]; caption: string; locationNames: Readonly<Record<string, Record<string, string>>> }) {
  const t = useTranslations("analytics.scopes");
  const locale = useLocale();
  const label = (s: ScopeRef) => (s.kind === "unit" ? pick(s.names, locale) : pick(locationNames[s.key] ?? {}, locale)) || s.key;
  return (
    <DataTable
      caption={caption}
      rows={scopes}
      rowKey={(s) => `${s.kind}:${s.key}`}
      emptyTitle={t("empty")}
      emptyMessage={t("emptyHint")}
      columns={[{ key: "name", header: t("column.scope"), rowHeader: true, cell: (s) => <a href={scopeHref(s)}>{label(s)}</a>, sortValue: label }]}
    />
  );
}

/**
 * One location or unit (spec 021 US6): each figure for the scope beside the organization's, "not enough data" under the
 * minimum group size, and the organization column left empty when it could be one other scope's number.
 */
export function ScopeDashboard({ view }: { view: ScopeDashboardView }) {
  const t = useTranslations("analytics.scopes");
  const locale = useLocale();
  const format = useFormatter();
  const percent = (n: number) => format.number(n, { style: "percent", maximumFractionDigits: 1 });
  const value = (n: number | null, of: number | null) =>
    n === null ? t("notEnough") : of === null ? format.number(n) : t("outOf", { value: format.number(n), of: format.number(of) });
  const rate = (key: string, n: number | null) => (n === null ? t("notEnough") : key === "open_requests" ? t("perPerson", { rate: format.number(n, { maximumFractionDigits: 2 }) }) : percent(n));
  return (
    <div className="analytics-item" data-testid="scope-dashboard">
      <dl className="analytics-stats">
        <div>
          <dt>{t("people")}</dt>
          <dd>{format.number(view.people)}</dd>
        </div>
        <div>
          <dt>{t("period")}</dt>
          <dd>
            {format.dateTimeRange(new Date(`${view.from}T00:00:00Z`), new Date(`${view.to}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" })}
          </dd>
        </div>
      </dl>
      {!view.enough ? <Alert tone="info" title={t("smallTitle")}>{t("small", { size: view.minGroupSize })}</Alert> : null}
      {view.enough && !view.groupShown ? <p className="analytics-meta">{t("groupHidden")}</p> : null}
      <div className="analytics-table">
        <table data-testid="scope-tiles">
          <caption>{t("figures")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("column.figure")}</th>
              <th scope="col">{t("column.value")}</th>
              <th scope="col">{t("column.rate")}</th>
              <th scope="col">{t("column.group")}</th>
            </tr>
          </thead>
          <tbody>
            {view.tiles.map((tile) => (
              <tr key={tile.key} data-testid={`scope-tile-${tile.key}`}>
                <th scope="row">{t(`tile.${tile.key}`)}</th>
                <td data-number>{value(tile.value, tile.of)}</td>
                <td data-number>{rate(tile.key, tile.rate)}</td>
                <td data-number>{tile.groupRate === null ? t("noGroup") : rate(tile.key, tile.groupRate)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {view.kpis && view.kpis.length > 0 ? (
        <div className="analytics-table">
          <table data-testid="scope-kpis">
            <caption>{t("kpisTitle")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("column.figure")}</th>
                <th scope="col">{t("column.value")}</th>
                <th scope="col">{t("kpiPeriod")}</th>
                <th scope="col">{t("column.source")}</th>
              </tr>
            </thead>
            <tbody>
              {view.kpis.map((k) => (
                <tr key={k.id} data-testid={`scope-kpi-${k.metric}`}>
                  <th scope="row">{pick(k.names, locale)}</th>
                  <td data-number>{k.value === null ? t("kpisEmpty") : `${format.number(k.value)} ${k.unit ?? ""}`.trim()}</td>
                  <td>
                    {k.periodStart && k.periodEnd
                      ? format.dateTimeRange(new Date(k.periodStart), new Date(k.periodEnd), { dateStyle: "medium", timeZone: "UTC" })
                      : null}
                  </td>
                  <td>
                    {k.source ? <span>{t("kpiSource", { source: k.source })}</span> : null}
                    {k.sourceTime ? <span className="analytics-meta">{t("kpiMeasured", { time: format.dateTime(new Date(k.sourceTime), { dateStyle: "medium", timeStyle: "short" }) })}</span> : null}
                    {k.receivedAt ? <span className="analytics-meta">{t("kpiReceived", { time: format.dateTime(new Date(k.receivedAt), { dateStyle: "medium", timeStyle: "short" }) })}</span> : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </div>
  );
}
