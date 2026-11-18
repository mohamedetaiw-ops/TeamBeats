"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { problemMessages } from "@/lib/notifications/types";
import { requestTone, type AttendanceRequest, type AttendanceRequestKind } from "@/lib/attendance";
import "./attendance.css";

type PunchDraft = { time: string; direction: "in" | "out" };

/**
 * Asking to change a day (spec 022 US3 scenarios 3 and 4): a correction adds the punches the day is missing, a
 * compensation day asks for work on a holiday or day off to be credited. Both go to the line manager; the API decides
 * which days qualify and how much a compensation day is worth.
 */
export function AttendanceRequests({ onChanged }: { onChanged?: () => void }) {
  const t = useTranslations("attendance");
  const format = useFormatter();
  const [kind, setKind] = useState<AttendanceRequestKind>("correction");
  const [date, setDate] = useState("");
  const [reason, setReason] = useState("");
  const [punches, setPunches] = useState<PunchDraft[]>([{ time: "", direction: "out" }]);
  const [list, setList] = useState<AttendanceRequest[] | null>(null);
  const [reload, setReload] = useState(0);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/me/attendance/requests", { cache: "no-store" });
        if (live && response.ok) setList((await response.json()) as AttendanceRequest[]);
      } catch {
        if (live) setList([]);
      }
    })();
    return () => {
      live = false;
    };
  }, [reload]);

  async function send() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/me/attendance/requests", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ kind, date: date || null, reason, punches: kind === "correction" ? punches : [] }),
      });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("requests.failed")]);
        return;
      }
      setNotice(t("requests.sent"));
      setReason("");
      setPunches([{ time: "", direction: "out" }]);
      setReload((n) => n + 1);
      onChanged?.();
    } catch {
      setMessages([t("requests.failed")]);
    } finally {
      setBusy(false);
    }
  }

  const setPunch = (i: number, patch: Partial<PunchDraft>) => setPunches((all) => all.map((p, j) => (j === i ? { ...p, ...patch } : p)));
  const day = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });

  return (
    <section className="attendance-section" aria-labelledby="attendance-requests">
      <h2 id="attendance-requests">{t("requests.title")}</h2>
      <p className="field-hint">{t("requests.intro")}</p>
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("requests.failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <RadioGroup
        legend={t("requests.kind")}
        name="request-kind"
        value={kind}
        onChange={(e) => setKind(e.target.value as AttendanceRequestKind)}
        options={(["correction", "compensation"] as const).map((k) => ({ value: k, label: t(`requests.kinds.${k}`), hint: t(`requests.kindHints.${k}`) }))}
      />
      <div className="ui-control-row">
        <FormField label={t("requests.date")} name="request-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
      </div>
      {kind === "correction"
        ? punches.map((p, i) => (
            <div className="ui-control-row" key={i} role="group" aria-label={t("requests.punch", { n: i + 1 })}>
              <FormField label={t("requests.time")} name={`punch-time-${i}`} type="time" value={p.time} onChange={(e) => setPunch(i, { time: e.target.value })} />
              <SelectField
                label={t("requests.direction")}
                name={`punch-direction-${i}`}
                value={p.direction}
                onChange={(e) => setPunch(i, { direction: e.target.value as "in" | "out" })}
                options={[
                  { value: "in", label: t("sign.in") },
                  { value: "out", label: t("sign.out") },
                ]}
              />
              {punches.length > 1 ? (
                <Button size="sm" variant="quiet" onClick={() => setPunches((all) => all.filter((_, j) => j !== i))}>
                  {t("requests.removePunch")}
                </Button>
              ) : null}
            </div>
          ))
        : null}
      {kind === "correction" && punches.length < 4 ? (
        <div className="ui-control-row">
          <Button size="sm" icon="plus" onClick={() => setPunches((all) => [...all, { time: "", direction: "in" }])}>
            {t("requests.addPunch")}
          </Button>
        </div>
      ) : null}
      <TextAreaField label={t("requests.reason")} name="request-reason" value={reason} maxLength={500} rows={3} onChange={(e) => setReason(e.target.value)} />
      <div className="ui-control-row">
        <Button variant="primary" busy={busy} busyLabel={t("sign.saving")} onClick={() => void send()}>
          {t("requests.send")}
        </Button>
      </div>

      <h3>{t("requests.mine")}</h3>
      {list && list.length > 0 ? (
        <ul className="attendance-requests">
          {list.map((r) => (
            <li key={r.id}>
              <Link href={`/attendance/requests/${r.id}`}>{t(`requests.kinds.${r.kind}`)}</Link>
              <span>{day(r.date)}</span>
              <Badge tone={requestTone[r.status]}>{t(`requests.statuses.${r.status}`)}</Badge>
            </li>
          ))}
        </ul>
      ) : (
        <p className="field-hint">{t("requests.none")}</p>
      )}
    </section>
  );
}
