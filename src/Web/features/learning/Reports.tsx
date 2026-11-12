"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Icon } from "@/components/ui/Icon";
import { SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import {
  call,
  certificateHref,
  learningApi,
  localText,
  reportHref,
  type CourseReport,
  type ReportCourse,
  type ReportPerson,
  type ReportState,
} from "@/lib/learning";
import { SourceText } from "./parts";
import "./learning.css";

export const reportTones: Record<ReportState, BadgeTone> = { assigned: "info", overdue: "danger", completed: "success", expired: "warning" };

/** The courses a compliance officer can report on (spec 028 US5). */
export function ReportCourses({ courses }: { courses: ReportCourse[] }) {
  const t = useTranslations("learning.reports");
  const locale = useLocale();
  if (courses.length === 0) return <StateView kind="empty" title={t("noCoursesTitle")} message={t("noCourses")} icon="book" />;
  return (
    <section className="learning-section" aria-labelledby="report-courses">
      <h2 id="report-courses">{t("courses")}</h2>
      <ul className="learning-list" aria-label={t("courses")}>
        {courses.map((c) => (
          <li key={c.id} className="learning-row">
            <Link href={reportHref(c.id)}>
              <bdi>{localText(c.titles, locale)}</bdi>
            </Link>
            {c.category ? (
              <span className="learning-meta">
                <bdi>{c.category}</bdi>
              </span>
            ) : null}
            {c.status === "retired" ? <Badge>{t("retired")}</Badge> : null}
          </li>
        ))}
      </ul>
    </section>
  );
}

/**
 * A course report (spec 028 US5, FR-013, FR-014): counts per unit in the viewer's scope, a parent unit counting the
 * units below it, the people of the chosen unit, and the evidence export. The API decides the scope and audits the export.
 */
export function CourseReportView({ report }: { report: CourseReport }) {
  const t = useTranslations("learning.reports");
  const locale = useLocale();
  const format = useFormatter();
  const [unit, setUnit] = useState(report.units[0]?.orgUnitId ?? "");
  const [people, setPeople] = useState<ReportPerson[] | "loading" | string[]>("loading");
  const query = unit ? `?unit=${encodeURIComponent(unit)}` : "";
  const base = `${learningApi}/reports/courses/${encodeURIComponent(report.courseId)}`;

  useEffect(() => {
    let live = true;
    void call<ReportPerson[]>(() => fetch(`${base}/people${query}`, { cache: "no-store" }), t("failed")).then((r) => {
      if (live) setPeople(r.ok ? r.value : r.messages);
    });
    return () => {
      live = false;
    };
  }, [base, query, t]);

  const unitName = (id: string) => localText(report.units.find((u) => u.orgUnitId === id)?.names, locale);
  const keys = ["assigned", "completed", "overdue", "expired"] as const;

  return (
    <div className="learning">
      <section className="learning-section" aria-labelledby="report-units">
        <h2 id="report-units">{t("byUnit")}</h2>
        <p className="learning-meta">{report.wholeTenant ? t("wholeTenant") : t("scoped")}</p>
        <div className="learning-row" role="group" aria-label={t("total")}>
          {keys.map((k) => (
            <Badge key={k} tone={reportTones[k]}>
              {t(`count.${k}`, { count: report.total[k] })}
            </Badge>
          ))}
        </div>
        {report.units.length === 0 ? (
          <StateView kind="empty" title={t("noUnitsTitle")} message={t("noUnits")} icon="people" />
        ) : (
          <div className="learning-table">
            <table>
              <caption className="learning-meta">{t("tableCaption")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("unit")}</th>
                  {keys.map((k) => (
                    <th key={k} scope="col">
                      {t(`column.${k}`)}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {report.units.map((u) => (
                  <tr key={u.orgUnitId}>
                    <th scope="row">
                      <bdi>{localText(u.names, locale)}</bdi>
                    </th>
                    {keys.map((k) => (
                      <td key={k} data-number>
                        {format.number(u.counts[k])}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="learning-section" aria-labelledby="report-people">
        <h2 id="report-people">{t("people")}</h2>
        <div className="learning-row">
          {report.units.length > 0 ? (
            <SelectField
              label={t("unit")}
              name="report-unit"
              value={unit}
              onChange={(e) => {
                setPeople("loading");
                setUnit(e.target.value);
              }}
              options={[
                ...(report.wholeTenant ? [{ value: "", label: t("everyone") }] : []),
                ...report.units.map((u) => ({ value: u.orgUnitId, label: localText(u.names, locale) })),
              ]}
            />
          ) : null}
          {/* A plain link, never prefetched: each export is audited. */}
          <a className="button button-secondary" data-variant="secondary" data-size="sm" data-icon href={`${base}/evidence${query}`} download>
            <Icon name="download" size={16} />
            <span>{t("export")}</span>
          </a>
        </div>
        <p className="learning-meta">{unit ? t("exportOf", { unit: unitName(unit) }) : t("exportAll")}</p>
        {people === "loading" ? (
          <StateView kind="loading" minBlockSize="8rem" />
        ) : people.length > 0 && typeof people[0] === "string" ? (
          <StateView kind="error" title={t("failed")} message={(people as string[]).join(" ")} />
        ) : people.length === 0 ? (
          <StateView kind="empty" title={t("noPeopleTitle")} message={t("noPeople")} icon="people" />
        ) : (
          <div className="learning-table">
            <table>
              <thead>
                <tr>
                  <th scope="col">{t("person")}</th>
                  <th scope="col">{t("state")}</th>
                  <th scope="col">{t("source")}</th>
                  <th scope="col">{t("due")}</th>
                  <th scope="col">{t("completed")}</th>
                  <th scope="col">{t("version")}</th>
                  <th scope="col">{t("score")}</th>
                  <th scope="col">{t("certificate")}</th>
                </tr>
              </thead>
              <tbody>
                {(people as ReportPerson[]).map((p) => (
                  <tr key={p.person.id}>
                    <th scope="row">
                      <bdi>{p.person.displayName}</bdi>
                    </th>
                    <td>
                      <Badge tone={reportTones[p.state]}>{t(`stateName.${p.state}`)}</Badge>
                    </td>
                    <td>
                      <SourceText source={p.source} />
                    </td>
                    <td>{p.dueOn ? format.dateTime(new Date(`${p.dueOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) : null}</td>
                    <td>{p.completedAt ? format.dateTime(new Date(p.completedAt), { dateStyle: "medium", timeStyle: "short" }) : null}</td>
                    <td data-number>{p.version ?? null}</td>
                    <td data-number>{p.score === null ? null : t("percent", { value: p.score })}</td>
                    <td>
                      {p.certificateId && p.certificateCode ? (
                        <Link href={certificateHref(p.certificateId)}>
                          <bdi>{p.certificateCode}</bdi>
                        </Link>
                      ) : null}
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
