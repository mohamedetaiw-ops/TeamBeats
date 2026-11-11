"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { DataTable } from "@/components/ui/DataTable";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import { thisMonth, type TeamAttendance as Team } from "@/lib/attendance";
import { MonthPicker } from "./AttendanceMonth";
import "./attendance.css";

type Row = Team["people"][number];

/** My team's month (US3 scenario 5): each direct report's hours and exceptions, with their days one link away. The API decides who is on the team. */
export function TeamAttendance() {
  const t = useTranslations("attendance");
  const format = useFormatter();
  const [month, setMonth] = useState(thisMonth());
  const [data, setData] = useState<Team | null>(null);
  const [messages, setMessages] = useState<string[] | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`/api/v1/hr/attendance/team?month=${month}`, { cache: "no-store" });
        if (!live) return;
        if (response.ok) {
          setData((await response.json()) as Team);
          setMessages(null);
        } else {
          setMessages(await problemMessages(response));
        }
      } catch {
        if (live) setMessages([t("failed")]);
      }
    })();
    return () => {
      live = false;
    };
  }, [month, t]);

  if (messages) return <StateView kind="error" message={messages.join(" ") || t("failed")} />;
  if (!data) return <StateView kind="loading" />;
  const hours = (n: number) => format.number(n, { maximumFractionDigits: 2 });
  const count = (key: "late" | "earlyLeave" | "missingPunch" | "absent" | "remote") => ({
    key,
    header: t(`totals.${key}`),
    align: "end" as const,
    sortValue: (r: Row) => r.totals[key],
    cell: (r: Row) => r.totals[key],
  });

  return (
    <div className="attendance">
      <section className="attendance-section" aria-labelledby="attendance-team">
        <h2 id="attendance-team">{t("team.month")}</h2>
        <MonthPicker month={month} onChange={setMonth} />
        <DataTable<Row>
          caption={t("team.caption")}
          rows={data.people}
          rowKey={(r) => r.person.id}
          defaultSort={{ key: "person", direction: "ascending" }}
          emptyTitle={t("team.empty")}
          columns={[
            {
              key: "person",
              header: t("team.person"),
              rowHeader: true,
              sortValue: (r) => r.person.displayName,
              cell: (r) => (
                <Link href={`/attendance/people/${r.person.id}?month=${data.month}`}>
                  <bdi>{r.person.displayName}</bdi>
                </Link>
              ),
            },
            { key: "days", header: t("totals.workedDays"), align: "end", sortValue: (r) => r.totals.workedDays, cell: (r) => r.totals.workedDays },
            {
              key: "hours",
              header: t("totals.hours"),
              align: "end",
              sortValue: (r) => r.totals.hoursWorked,
              cell: (r) => t("totals.hoursOf", { worked: hours(r.totals.hoursWorked), expected: hours(r.totals.expectedHours) }),
            },
            count("late"),
            count("earlyLeave"),
            count("missingPunch"),
            count("absent"),
            count("remote"),
          ]}
        />
      </section>
    </div>
  );
}
