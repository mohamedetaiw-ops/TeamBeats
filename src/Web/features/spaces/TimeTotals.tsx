"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button, ButtonLink } from "@/components/ui/Button";
import { BarList, ChartCard } from "@/components/ui/Charts";
import { FormField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { call, hoursOf, spacesApi, taskHref, type TimeTotals as Totals } from "@/lib/spaces";
import "./spaces.css";

/** Time logged in a space between two days, by person and by task, for its owners (spec 029 R3 US9 scenario 3). */
export function TimeTotals({ spaceId, initial }: { spaceId: string; initial: Totals }) {
  const t = useTranslations("spaces.time");
  const [totals, setTotals] = useState(initial);
  const [from, setFrom] = useState(initial.from);
  const [to, setTo] = useState(initial.to);
  const [messages, setMessages] = useState<string[]>([]);
  const duration = (minutes: number) => t("duration", hoursOf(minutes));
  const range = `from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`;

  async function load() {
    const result = await call<Totals>(() => fetch(`${spacesApi}/${spaceId}/time?${range}`), t("failed"));
    if (result.ok) {
      setMessages([]);
      setTotals(result.value);
    } else setMessages(result.messages);
  }

  return (
    <div className="spaces">
      <form
        className="spaces-toolbar"
        onSubmit={(e) => {
          e.preventDefault();
          void load();
        }}
      >
        <FormField label={t("from")} name="from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} required />
        <FormField label={t("to")} name="to" type="date" value={to} onChange={(e) => setTo(e.target.value)} required />
        <Button type="submit">{t("show")}</Button>
        <ButtonLink href={`${spacesApi}/${spaceId}/time.csv?${range}`} icon="download" download>
          {t("export")}
        </ButtonLink>
      </form>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {totals.minutes === 0 ? (
        <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} />
      ) : (
        <>
          <p className="spaces-meta">{t("total", { duration: duration(totals.minutes) })}</p>
          <div className="spaces-split">
            <ChartCard title={t("byPerson")} table={{ head: [t("person"), t("time")], rows: totals.people.map((p) => [p.person.displayName, duration(p.minutes)]) }}>
              <BarList label={t("byPerson")} items={totals.people.map((p) => ({ key: p.person.userId, label: p.person.displayName, value: p.minutes, valueText: duration(p.minutes) }))} />
            </ChartCard>
            <ChartCard title={t("byTask")}
              table={{ head: [t("task"), t("time"), t("estimate")], rows: totals.tasks.map((x) => [x.title, duration(x.minutes), x.estimateMinutes ? duration(x.estimateMinutes) : t("noEstimate")]) }}>
              <ul className="spaces-list" aria-label={t("byTask")}>
                {totals.tasks.map((x) => (
                  <li key={x.taskId} className="spaces-row">
                    <a href={taskHref(spaceId, x.taskId)}>
                      <bdi>{x.title}</bdi>
                    </a>
                    <span className="spaces-meta">
                      {x.estimateMinutes ? t("ofEstimate", { duration: duration(x.minutes), estimate: duration(x.estimateMinutes) }) : duration(x.minutes)}
                    </span>
                  </li>
                ))}
              </ul>
            </ChartCard>
          </div>
        </>
      )}
    </div>
  );
}
