"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import type { AttendanceMonth } from "@/lib/attendance";
import { AttendanceDays, AttendanceTotalsList, MonthPicker } from "./AttendanceMonth";
import "./attendance.css";

/** One person's month as their manager or an HR officer covering them sees it (US3 scenario 5). The API decides who may see it. */
export function PersonAttendance({ userId, initialMonth }: { userId: string; initialMonth: string }) {
  const t = useTranslations("attendance");
  const [month, setMonth] = useState(initialMonth);
  const [data, setData] = useState<AttendanceMonth | null>(null);
  const [messages, setMessages] = useState<string[] | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`/api/v1/hr/attendance/people/${encodeURIComponent(userId)}?month=${month}`, { cache: "no-store" });
        if (!live) return;
        if (response.ok) {
          setData((await response.json()) as AttendanceMonth);
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
  }, [userId, month, t]);

  if (messages) return <StateView kind="error" message={messages.join(" ") || t("failed")} />;
  if (!data) return <StateView kind="loading" />;
  return (
    <div className="attendance">
      <section className="attendance-section" aria-labelledby="attendance-person">
        <h2 id="attendance-person">
          <bdi>{data.person.displayName}</bdi>
        </h2>
        <MonthPicker month={month} onChange={setMonth} />
        <AttendanceTotalsList totals={data.totals} />
      </section>
      <AttendanceDays data={data} />
    </div>
  );
}
