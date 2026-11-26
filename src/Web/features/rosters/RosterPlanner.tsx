"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { problemMessages } from "@/lib/notifications/types";
import { addDays, patternName, type RosterPerson, type RosterWeek, type ShiftPattern } from "@/lib/rosters";
import "./rosters.css";

const OFF = "off";

type Draft = Record<string, Record<string, string>>;

function toDraft(week: RosterWeek): Draft {
  return Object.fromEntries(
    week.people.map((p) => [p.person.id, Object.fromEntries(p.days.filter((d) => d.patternId || d.dayOff).map((d) => [d.date, d.patternId ?? OFF]))]),
  );
}

/**
 * The manager's week (spec 022 US10): a shift or a day off for each direct report and day, with approved leave and
 * holidays shown, then publish. HR officers covering the team see it read only. The API checks every save again.
 */
export function RosterPlanner({ initial, patterns }: { initial: RosterWeek; patterns: ShiftPattern[] }) {
  const t = useTranslations("rosters");
  const locale = useLocale();
  const format = useFormatter();
  const id = useId();
  const [week, setWeek] = useState(initial);
  const [draft, setDraft] = useState<Draft>(() => toDraft(initial));
  const [busy, setBusy] = useState<"save" | "publish" | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const days = Array.from({ length: 7 }, (_, i) => addDays(week.weekStart, i));
  const byId = new Map(patterns.map((p) => [p.id, p]));
  const dayLabel = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00`), { weekday: "short", day: "numeric", month: "short" });

  async function call(kind: "save" | "publish") {
    setBusy(kind);
    setNotice(null);
    setMessages([]);
    try {
      const url = `/api/v1/hr/rosters/${week.weekStart}`;
      const entries = Object.entries(draft).flatMap(([userId, chosen]) =>
        Object.entries(chosen).map(([date, value]) => ({ userId, date, patternId: value === OFF ? null : value, dayOff: value === OFF })),
      );
      const response =
        kind === "save"
          ? await fetch(url, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ entries, version: week.status === "new" ? null : week.version }) })
          : await fetch(`${url}:publish`, { method: "POST" });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
        return;
      }
      const next = (await response.json()) as RosterWeek;
      setWeek(next);
      setDraft(toDraft(next));
      setNotice(kind === "save" ? t("saved") : t("published"));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function choose(person: string, date: string, value: string) {
    setDraft((all) => {
      const mine = { ...(all[person] ?? {}) };
      if (value) mine[date] = value;
      else delete mine[date];
      return { ...all, [person]: mine };
    });
  }

  const status = week.status === "published" ? (week.changed ? "changed" : "published") : week.status;
  const managerQuery = week.canEdit ? "" : `&managerId=${week.manager.id}`;

  return (
    <div className="rosters">
      <div className="rosters-row">
        <Link href={`/hr/rosters?week=${addDays(week.weekStart, -7)}${managerQuery}`}>{t("previous")}</Link>
        <strong id={`${id}-week`}>{t("weekOf", { date: format.dateTime(new Date(`${week.weekStart}T00:00:00`), { dateStyle: "medium" }) })}</strong>
        <Link href={`/hr/rosters?week=${addDays(week.weekStart, 7)}${managerQuery}`}>{t("next")}</Link>
        <Badge tone={status === "published" ? "success" : status === "changed" ? "warning" : "neutral"}>{t(`statuses.${status}`)}</Badge>
      </div>
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {week.canEdit && patterns.length === 0 ? <p className="notice">{t("noPatterns")}</p> : null}
      <DataTable<RosterPerson>
        caption={t("caption", { manager: week.manager.displayName })}
        rows={week.people}
        rowKey={(r) => r.person.id}
        density="compact"
        emptyTitle={t("empty")}
        columns={[
          { key: "person", header: t("person"), rowHeader: true, cell: (r) => <bdi>{r.person.displayName}</bdi> },
          ...days.map((date, i) => ({
            key: date,
            header: dayLabel(date),
            cell: (r: RosterPerson) => {
              const day = r.days[i]!;
              const value = draft[r.person.id]?.[date] ?? "";
              const planned = value && value !== OFF ? byId.get(value) : undefined;
              const away = day.leave || day.holiday;
              return (
                <div className="rosters-cell">
                  {week.canEdit ? (
                    <select
                      aria-label={t("cellLabel", { person: r.person.displayName, day: dayLabel(date) })}
                      value={value}
                      onChange={(e) => choose(r.person.id, date, e.target.value)}
                    >
                      <option value="">{t("none")}</option>
                      <option value={OFF}>{t("dayOff")}</option>
                      {patterns.map((p) => (
                        <option key={p.id} value={p.id}>
                          {t("option", { name: patternName(p, locale), range: t("hoursRange", { start: p.start, end: p.end }) })}
                        </option>
                      ))}
                    </select>
                  ) : (
                    <span>{planned ? t("option", { name: patternName(planned, locale), range: t("hoursRange", { start: planned.start, end: planned.end }) }) : value === OFF ? t("dayOff") : t("none")}</span>
                  )}
                  {planned ? <Badge tone={planned.tone}>{patternName(planned, locale)}</Badge> : null}
                  {day.leave ? <Badge tone={planned ? "danger" : "info"}>{t("onLeave")}</Badge> : null}
                  {day.holiday ? <Badge tone={planned ? "danger" : "info"}>{t("holiday")}</Badge> : null}
                  {planned && away ? <span className="field-hint">{t("conflict")}</span> : null}
                </div>
              );
            },
          })),
        ]}
      />
      {week.canEdit ? (
        <div className="rosters-row">
          <Button variant="secondary" busy={busy === "save"} busyLabel={t("saving")} onClick={() => void call("save")}>
            {t("save")}
          </Button>
          <Button variant="primary" busy={busy === "publish"} busyLabel={t("publishing")} disabled={week.status === "new"} onClick={() => void call("publish")}>
            {t("publish")}
          </Button>
          <p className="field-hint">{t("publishHint")}</p>
        </div>
      ) : null}
    </div>
  );
}
