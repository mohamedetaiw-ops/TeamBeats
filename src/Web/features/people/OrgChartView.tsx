"use client";

import Link from "next/link";
import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { personName, type OrgChart, type PersonCard } from "@/lib/people/types";
import { PersonItem } from "./PersonItem";
import "./people.css";

/**
 * Org chart (spec 010 US3, FR-007): the manager chain to the top, the person with their peers, and their direct reports,
 * as a chart or as a list with the same content. Every node is a link, so the chart is moved through with Tab, and
 * logical properties mirror it in RTL. Large teams show a count with a link to the full list.
 */
export function OrgChartView({ chart, reportsHref }: { chart: OrgChart; reportsHref: string | null }) {
  const t = useTranslations("people.org");
  const locale = useLocale();
  const [view, setView] = useState<"chart" | "list">("chart");
  const chain = [...chart.chain].reverse();
  const morePeers = chart.peerCount - chart.peers.length;
  const moreReports = chart.reportCount - chart.reports.length;

  const node = (p: PersonCard, reports: number | null, current = false) => (
    <div className="people-chart-node" aria-current={current ? "true" : undefined}>
      <Link href={`/people/${p.id}`} aria-label={reports !== null ? t("managerOf", { name: personName(p, locale), count: reports }) : undefined}>
        <bdi>{personName(p, locale)}</bdi>
      </Link>
      {p.jobTitle ? (
        <div className="people-meta">
          <bdi>{p.jobTitle}</bdi>
        </div>
      ) : null}
    </div>
  );

  return (
    <section aria-labelledby="people-org-title" className="people-section">
      <div className="people-head">
        <h2 id="people-org-title">{t("title")}</h2>
        <fieldset className="people-toggle">
          <legend className="visually-hidden">{t("view")}</legend>
          {(["chart", "list"] as const).map((v) => (
            <button key={v} type="button" className="button button-secondary" aria-pressed={view === v} onClick={() => setView(v)}>
              {t(v)}
            </button>
          ))}
        </fieldset>
      </div>

      {view === "chart" ? (
        <div className="people-chart" data-testid="org-chart">
          {chain.length > 0 ? (
            <ol className="people-chart" aria-label={t("chain")}>
              {chain.map((p) => (
                <li key={p.id} className="people-chart">
                  {node(p, null)}
                  <span className="people-chart-link" aria-hidden="true" />
                </li>
              ))}
            </ol>
          ) : null}
          <ul className="people-chart-row" aria-label={t("peers")}>
            {[chart.person, ...chart.peers].map((p) => (
              <li key={p.id}>{node(p, p.id === chart.person.id ? chart.reportCount : null, p.id === chart.person.id)}</li>
            ))}
            {morePeers > 0 ? <li className="people-meta">{t("more", { count: morePeers })}</li> : null}
          </ul>
          {chart.reports.length > 0 ? (
            <>
              <span className="people-chart-link" aria-hidden="true" />
              <ul className="people-chart-row" aria-label={t("reports")}>
                {chart.reports.map((p) => (
                  <li key={p.id}>{node(p, null)}</li>
                ))}
              </ul>
            </>
          ) : null}
          {moreReports > 0 && reportsHref ? <Link href={reportsHref}>{t("seeAll")}</Link> : null}
        </div>
      ) : (
        <div className="people-form" data-testid="org-list">
          <OrgList title={t("chain")} people={chain} empty={t("none")} locale={locale} />
          <OrgList title={t("peers")} people={chart.peers} empty={t("none")} locale={locale} more={morePeers > 0 ? t("more", { count: morePeers }) : null} />
          <OrgList title={t("reports")} people={chart.reports} empty={t("none")} locale={locale} more={moreReports > 0 ? t("more", { count: moreReports }) : null} />
          {moreReports > 0 && reportsHref ? <Link href={reportsHref}>{t("seeAll")}</Link> : null}
        </div>
      )}
    </section>
  );
}

function OrgList({ title, people, empty, locale, more }: { title: string; people: PersonCard[]; empty: string; locale: string; more?: string | null }) {
  return (
    <div className="people-form-row">
      <h3>{title}</h3>
      {people.length === 0 ? (
        <p>{empty}</p>
      ) : (
        <ul className="people-list">
          {people.map((p) => (
            <li key={p.id}>
              <PersonItem person={p} locale={locale} />
            </li>
          ))}
        </ul>
      )}
      {more ? <p className="people-meta">{more}</p> : null}
    </div>
  );
}
