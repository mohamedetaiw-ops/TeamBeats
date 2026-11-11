"use client";

import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { addMonths, flagTone, type AttendanceDay, type AttendanceMonth as Month, type AttendanceTotals } from "@/lib/attendance";
import "./attendance.css";

/** Month picker shared by the attendance pages; months are yyyy-mm as the API takes them. */
export function MonthPicker({ month, onChange }: { month: string; onChange: (month: string) => void }) {
  const t = useTranslations("attendance");
  const format = useFormatter();
  const label = format.dateTime(new Date(`${month}-01T00:00:00Z`), { month: "long", year: "numeric", timeZone: "UTC" });
  return (
    <div className="attendance-row" role="group" aria-label={t("month")}>
      <Button size="sm" onClick={() => onChange(addMonths(month, -1))}>
        {t("previous")}
      </Button>
      <p className="attendance-month" aria-live="polite">
        {label}
      </p>
      <Button size="sm" onClick={() => onChange(addMonths(month, 1))} iconEnd="chevron-forward">
        {t("next")}
      </Button>
    </div>
  );
}

/** The month's totals (US3 scenario 2): hours against expected and how many days showed each exception. */
export function AttendanceTotalsList({ totals }: { totals: AttendanceTotals }) {
  const t = useTranslations("attendance");
  const format = useFormatter();
  const hours = (n: number) => format.number(n, { maximumFractionDigits: 2 });
  const items: [string, string][] = [
    [t("totals.workedDays"), String(totals.workedDays)],
    [t("totals.hours"), t("totals.hoursOf", { worked: hours(totals.hoursWorked), expected: hours(totals.expectedHours) })],
    [t("totals.late"), String(totals.late)],
    [t("totals.earlyLeave"), String(totals.earlyLeave)],
    [t("totals.missingPunch"), String(totals.missingPunch)],
    [t("totals.absent"), String(totals.absent)],
    [t("totals.remote"), String(totals.remote)],
    [t("totals.workedDaysOff"), String(totals.workedDaysOff)],
  ];
  return (
    <dl className="attendance-totals">
      {items.map(([k, v]) => (
        <div key={k}>
          <dt>{k}</dt>
          <dd>{v}</dd>
        </div>
      ))}
    </dl>
  );
}

/** One person's month, day by day, as the API computed it from punches, the working hours rule, the calendar and approved leave. */
export function AttendanceDays({ data }: { data: Month }) {
  const t = useTranslations("attendance");
  const format = useFormatter();
  const hours = (n: number) => format.number(n, { maximumFractionDigits: 2 });
  const date = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { weekday: "short", day: "numeric", month: "short", timeZone: "UTC" });
  const shown = data.days.filter((d) => d.kind !== "day_off" || d.punches.length > 0);
  return (
    <section className="attendance-section" aria-labelledby="attendance-days">
      <h2 id="attendance-days">{t("days.title")}</h2>
      {data.rule ? (
        <p className="field-hint">{t("days.rule", { start: data.rule.start, end: data.rule.end, late: data.rule.graceLateMinutes, early: data.rule.graceEarlyMinutes })}</p>
      ) : (
        <p className="field-hint">{t("days.defaultRule")}</p>
      )}
      <DataTable<AttendanceDay>
        caption={t("days.caption", { name: data.person.displayName })}
        rows={shown}
        rowKey={(d) => d.date}
        density="compact"
        emptyTitle={t("days.empty")}
        columns={[
          { key: "date", header: t("days.date"), rowHeader: true, sortValue: (d) => d.date, cell: (d) => date(d.date) },
          { key: "kind", header: t("days.kind"), cell: (d) => t(`kinds.${d.kind}`) },
          { key: "in", header: t("days.firstIn"), cell: (d) => (d.firstIn ? <bdi dir="ltr">{d.firstIn}</bdi> : null) },
          { key: "out", header: t("days.lastOut"), cell: (d) => (d.lastOut ? <bdi dir="ltr">{d.lastOut}</bdi> : null) },
          { key: "hours", header: t("days.hours"), align: "end", sortValue: (d) => d.hoursWorked, cell: (d) => hours(d.hoursWorked) },
          { key: "expected", header: t("days.expected"), align: "end", cell: (d) => hours(d.expectedHours) },
          {
            key: "flags",
            header: t("days.flags"),
            cell: (d) =>
              d.flags.length > 0 ? (
                <span className="attendance-flags">
                  {d.flags.map((f) => (
                    <Badge key={f} tone={flagTone[f]}>
                      {t(`flags.${f}`)}
                    </Badge>
                  ))}
                </span>
              ) : null,
          },
        ]}
      />
    </section>
  );
}
