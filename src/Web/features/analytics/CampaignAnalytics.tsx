"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import {
  campaignAnalyticsHref,
  notificationChannels,
  otherGroup,
  type AnalyticsCampaign,
  type CampaignAnalyticsView,
} from "@/lib/analytics/types";
import { BarChart, ChartCard, FunnelChart, seriesTable } from "@/components/ui/Charts";
import { DataTable } from "@/components/ui/DataTable";
import { ButtonLink } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** The campaigns the person runs, with two picked to compare (spec 021 US2 scenario 3). */
export function CampaignList({ campaigns }: { campaigns: readonly AnalyticsCampaign[] }) {
  const t = useTranslations("analytics.campaigns");
  const format = useFormatter();
  const [selected, setSelected] = useState<string[]>([]);
  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium" }) : t("notSent"));
  const pair = selected.slice(-2);
  return (
    <DataTable
      caption={t("listCaption")}
      rows={campaigns}
      rowKey={(c) => c.id}
      rowLabel={(c) => c.name}
      selected={pair}
      onSelectedChange={(keys) => setSelected(keys.slice(-2))}
      selection={
        pair.length === 2 ? (
          <ButtonLink variant="primary" size="sm" href={`/analytics/campaigns/compare?first=${pair[0]}&second=${pair[1]}`} data-testid="campaigns-compare">
            {t("compare")}
          </ButtonLink>
        ) : (
          <p className="analytics-meta">{t("compareHint")}</p>
        )
      }
      emptyTitle={t("empty")}
      emptyMessage={t("emptyHint")}
      columns={[
        { key: "name", header: t("column.name"), rowHeader: true, cell: (c) => <a href={campaignAnalyticsHref(c.id)}>{c.name}</a>, sortValue: (c) => c.name },
        { key: "status", header: t("column.status"), cell: (c) => t(`status.${c.status}`) },
        { key: "steps", header: t("column.steps"), align: "end", cell: (c) => format.number(c.steps), sortValue: (c) => c.steps },
        { key: "sent", header: t("column.firstSent"), cell: (c) => date(c.firstSentAt), sortValue: (c) => c.firstSentAt ?? "" },
      ]}
    />
  );
}

/**
 * One campaign's analytics (spec 021 US2): the funnel in total, each step, deliveries by channel, the unit and location
 * breakdown and return on effort. Figures under the minimum group size arrive as null and are shown as hidden.
 */
export function CampaignDashboard({ view, headingLevel = 2 }: { view: CampaignAnalyticsView; headingLevel?: 2 | 3 }) {
  const t = useTranslations("analytics.campaigns");
  const locale = useLocale();
  const format = useFormatter();
  const number = (n: number) => format.number(n);
  const hidden = t("hidden", { size: view.minGroupSize });
  const count = (n: number | null) => (n === null ? hidden : number(n));
  const share = (n: number | null) => (n === null ? hidden : format.number(n, { style: "percent", maximumFractionDigits: 1 }));
  const money = (n: number | null) =>
    n === null || !view.effort ? t("effort.none") : format.number(n, { style: "currency", currency: view.effort.currency, maximumFractionDigits: 2 });
  const H = `h${headingLevel}` as const;
  const total = view.total;
  const stages =
    total.viewed === null || total.clicked === null || total.completed === null
      ? []
      : [
          { key: "delivered", label: t("stage.delivered"), value: total.people },
          { key: "viewed", label: t("stage.viewed"), value: total.viewed },
          { key: "clicked", label: t("stage.clicked"), value: total.clicked },
          { key: "completed", label: t("stage.completed"), value: total.completed },
        ];
  const channels = notificationChannels.filter((c) => (view.channels[c] ?? 0) > 0);
  const channelSeries = [{ key: "delivered", label: t("stage.delivered"), values: channels.map((c) => view.channels[c] ?? 0) }];
  const groupLabel = (g: CampaignAnalyticsView["groups"][number]) =>
    g.group === otherGroup ? t("other", { size: view.minGroupSize }) : g.dimension === "unit" ? pick(g.names, locale) || g.group : g.group;

  return (
    <div className="analytics-item" data-testid="campaign-dashboard">
      <section className="analytics-card" aria-labelledby={`campaign-total-${view.campaign.id}`}>
        <H id={`campaign-total-${view.campaign.id}`}>{t("total")}</H>
        <dl className="analytics-stats">
          <div>
            <dt>{t("stage.delivered")}</dt>
            <dd>{number(total.people)}</dd>
          </div>
          <div>
            <dt>{t("viewedShare")}</dt>
            <dd>{share(total.viewedShare)}</dd>
          </div>
          <div>
            <dt>{t("completedShare")}</dt>
            <dd data-testid="campaign-completed-share">{share(total.completedShare)}</dd>
          </div>
        </dl>
        {stages.length > 0 ? (
          <FunnelChart label={t("funnelSummary", { people: number(total.people) })} stages={stages} format={number} />
        ) : (
          <StateView kind="empty" title={t("suppressed")} message={t("suppressedHint", { size: view.minGroupSize })} />
        )}
      </section>

      <div className="analytics-table">
        <table data-testid="campaign-steps">
          <caption className="visually-hidden">{t("stepsCaption")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("column.step")}</th>
              <th scope="col">{t("stage.delivered")}</th>
              <th scope="col">{t("stage.viewed")}</th>
              <th scope="col">{t("stage.clicked")}</th>
              <th scope="col">{t("stage.completed")}</th>
            </tr>
          </thead>
          <tbody>
            {view.steps.map((s) => (
              <tr key={s.stepId}>
                <th scope="row">
                  {t("stepName", { position: s.position, title: pick(s.titles, locale) })}
                  {s.acknowledgementRequired ? <span className="analytics-meta"> {t("confirms")}</span> : null}
                </th>
                <td data-number>{number(s.counts.people)}</td>
                <td data-number>{count(s.counts.viewed)}</td>
                <td data-number>{count(s.counts.clicked)}</td>
                <td data-number>{count(s.counts.completed)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="analytics-charts">
        {channels.length > 0 ? (
          <ChartCard
            title={t("channels")}
            table={seriesTable(t("column.channel"), channels.map((c) => t(`channel.${c}`)), channelSeries, number)}
          >
            <BarChart label={t("channelsSummary", { count: channels.length })} categories={channels.map((c) => t(`channel.${c}`))} series={channelSeries} format={number} />
          </ChartCard>
        ) : null}
        <section className="analytics-card" aria-labelledby={`campaign-effort-${view.campaign.id}`} data-testid="campaign-effort">
          <H id={`campaign-effort-${view.campaign.id}`}>{t("effort.title")}</H>
          {view.effort ? (
            <dl className="analytics-stats">
              <div>
                <dt>{t("effort.total")}</dt>
                <dd>{money(view.effort.totalCost)}</dd>
              </div>
              <div>
                <dt>{t("effort.perReached")}</dt>
                <dd>{money(view.effort.perReached)}</dd>
              </div>
              <div>
                <dt>{t("effort.perCompleted")}</dt>
                <dd>{money(view.effort.perCompleted)}</dd>
              </div>
            </dl>
          ) : (
            <p className="analytics-meta">{t("effort.missing")}</p>
          )}
        </section>
      </div>

      {(["unit", "location"] as const).map((dimension) => {
        const rows = view.groups.filter((g) => g.dimension === dimension);
        if (rows.length === 0) return null;
        return (
          <section key={dimension} className="analytics-card" aria-labelledby={`campaign-${dimension}-${view.campaign.id}`}>
            <H id={`campaign-${dimension}-${view.campaign.id}`}>{t(`by.${dimension}`)}</H>
            <p className="analytics-meta">{t("otherNote", { size: view.minGroupSize })}</p>
            <div className="analytics-table">
              <table data-testid={`campaign-${dimension}`}>
                <thead>
                  <tr>
                    <th scope="col">{t(`column.${dimension}`)}</th>
                    <th scope="col">{t("stage.delivered")}</th>
                    <th scope="col">{t("stage.viewed")}</th>
                    <th scope="col">{t("stage.completed")}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((g) => (
                    <tr key={g.group}>
                      <th scope="row">
                        <bdi>{groupLabel(g)}</bdi>
                      </th>
                      <td data-number>{number(g.audience)}</td>
                      <td data-number>{number(g.viewed)}</td>
                      <td data-number>{number(g.completed)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        );
      })}
    </div>
  );
}
