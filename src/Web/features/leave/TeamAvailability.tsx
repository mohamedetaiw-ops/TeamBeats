"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import { addDays, datesBetween, todayIso, type LeaveAbsence, type TeamAvailability as Availability } from "@/lib/leave";
import "./leave.css";

const span = 14;

/**
 * Team availability (spec 022 US1 scenario 3, FR-004): who on my team is away on which day, approved and pending. The
 * API decides who is on the team and hides the type of a sensitive leave, which then shows only as away.
 */
export function TeamAvailability() {
  const t = useTranslations("leave.team");
  const format = useFormatter();
  const [from, setFrom] = useState(todayIso());
  const [data, setData] = useState<Availability | null>(null);
  const [messages, setMessages] = useState<string[] | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const to = addDays(from, span - 1);
        const response = await fetch(`/api/v1/hr/leave/team?from=${from}&to=${to}`, { cache: "no-store" });
        if (!live) return;
        if (response.ok) {
          setData((await response.json()) as Availability);
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
  }, [from, t]);

  if (messages) return <StateView kind="error" message={messages.join(" ") || t("failed")} />;
  if (!data) return <StateView kind="loading" />;

  const days = datesBetween(data.from, data.to);
  const byKey = new Map<string, LeaveAbsence>(data.absences.map((a) => [`${a.userId}|${a.date}`, a]));
  const day = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { weekday: "short", day: "numeric", timeZone: "UTC" });
  const long = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "full", timeZone: "UTC" });

  return (
    <div className="leave">
      <div className="ui-control-row">
        <FormField label={t("from")} name="from" type="date" value={from} onChange={(e) => e.target.value && setFrom(e.target.value)} />
        <Button onClick={() => setFrom(addDays(from, -span))}>
          {t("earlier")}
        </Button>
        <Button onClick={() => setFrom(addDays(from, span))} iconEnd="chevron-forward">
          {t("later")}
        </Button>
      </div>
      <p className="field-hint">{t("legend")}</p>
      <div className="leave-grid-wrap" tabIndex={0} role="region" aria-label={t("caption")}>
        <table className="leave-grid">
          <caption className="visually-hidden">{t("caption")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("person")}</th>
              {days.map((d) => (
                <th key={d} scope="col" abbr={long(d)}>
                  {day(d)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {data.people.map((p) => (
              <tr key={p.userId}>
                <th scope="row">
                  <bdi>{p.displayName}</bdi>
                </th>
                {days.map((d) => {
                  const a = byKey.get(`${p.userId}|${d}`);
                  if (!a) return <td key={d} />;
                  const label = a.status === "approved" ? (a.fraction < 1 ? t("halfAway") : t("away")) : t("pending");
                  return (
                    <td key={d} data-status={a.status} data-half={a.fraction < 1 || undefined}>
                      <span className="visually-hidden">{label}</span>
                      <span aria-hidden="true">{a.status === "approved" ? "●" : "○"}</span>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
