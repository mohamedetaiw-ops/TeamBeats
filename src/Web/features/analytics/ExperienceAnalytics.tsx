"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { noUnitGroup, otherGroup, type ExperienceAnalyticsView, type PersonalizationCounts } from "@/lib/analytics/types";
import { Badge } from "@/components/ui/Badge";
import { StateView } from "@/components/ui/StateView";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

const dimensions = ["device", "theme", "unit", "template", "release"] as const;

/**
 * Experience dashboards (spec 021 US9): template use by owning unit, layouts, personalization against use and resets,
 * p75 Core Web Vitals with the published variants that miss the spec 032 targets, and task flows.
 */
export function ExperienceDashboard({ view }: { view: ExperienceAnalyticsView }) {
  const t = useTranslations("analytics.experience");
  const locale = useLocale();
  const format = useFormatter();
  const number = (n: number | null) => (n === null ? t("hidden", { size: view.minGroupSize }) : format.number(n));
  const percent = (n: number | null) => (n === null ? t("hidden", { size: view.minGroupSize }) : format.number(n, { style: "percent", maximumFractionDigits: 1 }));
  const ms = (n: number | null) => (n === null ? t("noSample") : t("ms", { value: format.number(n) }));
  const cls = (n: number | null) => (n === null ? t("noSample") : format.number(n, { maximumFractionDigits: 3 }));
  const unit = (group: string, names: Record<string, string>) =>
    group === otherGroup ? t("other", { size: view.minGroupSize }) : group === noUnitGroup ? t("noUnit") : pick(names, locale) || group;
  const unitNames = Object.fromEntries([...view.scopes.map((s) => [s.unit, s.names]), ...view.personalization.units.map((u) => [u.group, u.names])]);
  const group = (dimension: (typeof dimensions)[number], g: string) =>
    g === noUnitGroup ? t(dimension === "unit" ? "noUnit" : "notRecorded") : dimension === "unit" ? unit(g, unitNames[g] ?? {}) : dimension === "release" ? t("release", { release: g }) : g;
  const counts = (c: PersonalizationCounts) => (
    <>
      <td data-number>{format.number(c.people)}</td>
      <td data-number>{number(c.customized)}</td>
      <td data-number>{number(c.reset)}</td>
    </>
  );

  return (
    <div className="analytics-item" data-testid="experience-dashboard">
      <section className="analytics-card" aria-labelledby="experience-templates">
        <h2 id="experience-templates">{t("templates")}</h2>
        {view.templates.length === 0 ? (
          <StateView kind="empty" title={t("noPages")} message={t("noPagesHint")} />
        ) : (
          <div className="analytics-table">
            <table data-testid="experience-templates">
              <thead>
                <tr>
                  <th scope="col">{t("column.template")}</th>
                  <th scope="col">{t("column.version")}</th>
                  <th scope="col">{t("column.pages")}</th>
                  <th scope="col">{t("column.published")}</th>
                </tr>
              </thead>
              <tbody>
                {view.templates.map((r) => (
                  <tr key={`${r.template}@${r.version}`}>
                    <th scope="row">
                      <bdi>{r.template}</bdi>
                    </th>
                    <td>
                      <bdi>{r.version}</bdi>
                    </td>
                    <td data-number>{format.number(r.pages)}</td>
                    <td data-number>{format.number(r.published)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {view.layouts.length > 0 ? (
          <div className="analytics-table">
            <table data-testid="experience-layouts">
              <caption>{t("layouts")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("column.layout")}</th>
                  <th scope="col">{t("column.pages")}</th>
                </tr>
              </thead>
              <tbody>
                {view.layouts.map((l) => (
                  <tr key={l.key}>
                    <th scope="row">
                      <bdi>{l.key === noUnitGroup ? t("notRecorded") : l.key}</bdi>
                    </th>
                    <td data-number>{format.number(l.pages)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        {view.scopes.length > 0 ? (
          <div className="analytics-table">
            <table data-testid="experience-scopes">
              <caption>{t("scopes")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("column.unit")}</th>
                  <th scope="col">{t("column.templates")}</th>
                </tr>
              </thead>
              <tbody>
                {view.scopes.map((s) => (
                  <tr key={s.unit}>
                    <th scope="row">
                      <bdi>{unit(s.unit, s.names)}</bdi>
                    </th>
                    <td>
                      <bdi>{s.templates.map((x) => t("templateCount", { template: x.key, pages: format.number(x.pages) })).join(", ")}</bdi>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </section>

      <section className="analytics-card" aria-labelledby="experience-personalization">
        <h2 id="experience-personalization">
          {t("personalization")} <Badge tone={view.personalization.enabled ? "success" : "neutral"}>{t(view.personalization.enabled ? "enabled" : "disabled")}</Badge>
        </h2>
        <p className="analytics-meta">{t("personalizationNote", { size: view.minGroupSize })}</p>
        <div className="analytics-table">
          <table data-testid="experience-personalization">
            <thead>
              <tr>
                <th scope="col">{t("column.unit")}</th>
                <th scope="col">{t("column.people")}</th>
                <th scope="col">{t("column.customized")}</th>
                <th scope="col">{t("column.reset")}</th>
              </tr>
            </thead>
            <tbody>
              {view.personalization.units.map((u) => (
                <tr key={u.group}>
                  <th scope="row">
                    <bdi>{unit(u.group, u.names)}</bdi>
                  </th>
                  {counts(u.counts)}
                </tr>
              ))}
              <tr>
                <th scope="row">{t("allUnits")}</th>
                {counts(view.personalization.total)}
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section className="analytics-card" aria-labelledby="experience-performance">
        <h2 id="experience-performance">{t("performance")}</h2>
        <p className="analytics-meta">{t("performanceNote", { samples: format.number(view.performance.samples) })}</p>
        {view.performance.flags.length > 0 ? (
          <div className="analytics-table">
            <table data-testid="experience-flags">
              <caption>{t("flags")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("column.template")}</th>
                  <th scope="col">{t("column.device")}</th>
                  <th scope="col">{t("vital.lcp")}</th>
                  <th scope="col">{t("vital.inp")}</th>
                  <th scope="col">{t("vital.cls")}</th>
                </tr>
              </thead>
              <tbody>
                {view.performance.flags.map((f) => (
                  <tr key={`${f.pageId}:${f.release}:${f.deviceClass}`}>
                    <th scope="row">
                      <bdi>{f.template}</bdi> {f.release !== null ? t("release", { release: String(f.release) }) : null}
                    </th>
                    <td>{t(`device.${f.deviceClass}`)}</td>
                    <td data-number>{ms(f.lcp)} {f.missed.includes("lcp") ? <Badge tone="warning">{t("missed")}</Badge> : null}</td>
                    <td data-number>{ms(f.inp)} {f.missed.includes("inp") ? <Badge tone="warning">{t("missed")}</Badge> : null}</td>
                    <td data-number>{cls(f.cls)} {f.missed.includes("cls") ? <Badge tone="warning">{t("missed")}</Badge> : null}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        {dimensions.map((d) => {
          const rows = view.performance.rows.filter((r) => r.dimension === d);
          if (rows.length === 0) return null;
          return (
            <div key={d} className="analytics-table">
              <table data-testid={`experience-vitals-${d}`}>
                <caption>{t(`by.${d}`)}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t(`dimension.${d}`)}</th>
                    <th scope="col">{t("column.samples")}</th>
                    <th scope="col">{t("vital.lcp")}</th>
                    <th scope="col">{t("vital.inp")}</th>
                    <th scope="col">{t("vital.cls")}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.group}>
                      <th scope="row">
                        <bdi>{d === "device" ? t(`device.${r.group}`) : group(d, r.group)}</bdi>
                      </th>
                      <td data-number>{format.number(r.samples)}</td>
                      <td data-number>{ms(r.lcp)}</td>
                      <td data-number>{ms(r.inp)}</td>
                      <td data-number>{cls(r.cls)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          );
        })}
      </section>

      <section className="analytics-card" aria-labelledby="experience-outcomes">
        <h2 id="experience-outcomes">{t("outcomes")}</h2>
        {view.outcomes.length === 0 ? (
          <p className="analytics-meta">{t("noOutcomes")}</p>
        ) : (
          <div className="analytics-table">
            <table data-testid="experience-outcomes">
              <thead>
                <tr>
                  <th scope="col">{t("column.flow")}</th>
                  <th scope="col">{t("column.started")}</th>
                  <th scope="col">{t("column.completed")}</th>
                  <th scope="col">{t("column.abandoned")}</th>
                  <th scope="col">{t("column.rate")}</th>
                  <th scope="col">{t("column.dropOff")}</th>
                </tr>
              </thead>
              <tbody>
                {view.outcomes.map((f) => (
                  <tr key={f.flow}>
                    <th scope="row">
                      <bdi>{f.flow}</bdi>
                    </th>
                    <td data-number>{format.number(f.started)}</td>
                    <td data-number>{format.number(f.completed)}</td>
                    <td data-number>{format.number(f.abandoned)}</td>
                    <td data-number>{percent(f.completionRate)}</td>
                    <td>
                      <bdi>{f.dropOffStep ?? t("noDropOff")}</bdi>
                    </td>
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
