"use client";

import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { statusTone, type LeaveRequest, type MyLeave as MyLeaveData } from "@/lib/leave";
import { LeaveRequestForm } from "./LeaveRequestForm";
import "./leave.css";

type View = { kind: "loading" } | { kind: "error"; messages: string[] } | { kind: "ready"; data: MyLeaveData };

/**
 * My leave (spec 022 US1): balances per type and leave year as the API counts them, the request form, my requests and
 * the requests where a colleague named me as their replacement.
 */
export function MyLeave() {
  const t = useTranslations("leave");
  const locale = useLocale();
  const format = useFormatter();
  const [view, setView] = useState<View>({ kind: "loading" });
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const [version, setVersion] = useState(0);
  const load = () => setVersion((v) => v + 1);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/me/leave", { cache: "no-store" });
        if (!live) return;
        if (response.ok) return setView({ kind: "ready", data: (await response.json()) as MyLeaveData });
        const messages = await problemMessages(response);
        setView({ kind: "error", messages: messages.length > 0 ? messages : [t("failed")] });
      } catch {
        if (live) setView({ kind: "error", messages: [t("failed")] });
      }
    })();
    return () => {
      live = false;
    };
  }, [t, version]);

  async function answer(request: LeaveRequest, accept: boolean) {
    setBusy(request.id);
    try {
      const response = await fetch(`/api/v1/hr/leave/requests/${request.id}/replacement:answer`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ accept }),
      });
      setNotice(response.ok ? (accept ? t("replacement.accepted") : t("replacement.declined")) : ((await problemMessages(response))[0] ?? t("failed")));
      load();
    } finally {
      setBusy(null);
    }
  }

  const date = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const number = (n: number) => format.number(n, { maximumFractionDigits: 2 });
  const when = (at: string) => format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" });
  const range = (r: LeaveRequest) => (r.start === r.end ? date(r.start) : t("range", { from: date(r.start), to: date(r.end) }));

  if (view.kind === "loading") return <StateView kind="loading" />;
  if (view.kind === "error") return <StateView kind="error" message={view.messages.join(" ")} />;
  const { data } = view;

  const columns: Column<LeaveRequest>[] = [
    { key: "number", header: t("columns.number"), cell: (r) => <a href={`/leave/requests/${r.id}`}>{r.number}</a>, rowHeader: true },
    { key: "type", header: t("columns.type"), cell: (r) => <bdi>{pick(r.typeNames, locale, r.typeKey)}</bdi> },
    { key: "dates", header: t("columns.dates"), cell: range },
    { key: "days", header: t("columns.days"), cell: (r) => number(r.days), align: "end" },
    { key: "status", header: t("columns.status"), cell: (r) => <Badge tone={statusTone[r.status]}>{t(`status.${r.status}`)}</Badge> },
  ];
  const askColumns: Column<LeaveRequest>[] = [
    { key: "person", header: t("columns.person"), cell: (r) => <bdi>{r.person.displayName}</bdi>, rowHeader: true },
    { key: "dates", header: t("columns.dates"), cell: range },
    {
      key: "answer",
      header: t("columns.answer"),
      cell: (r) =>
        r.replacementAnswer ? (
          t(`replacement.${r.replacementAnswer}`)
        ) : (
          <span className="leave-actions">
            <Button size="sm" variant="primary" busy={busy === r.id} onClick={() => void answer(r, true)}>
              {t("replacement.accept")}
            </Button>
            <Button size="sm" disabled={busy === r.id} onClick={() => void answer(r, false)}>
              {t("replacement.decline")}
            </Button>
          </span>
        ),
    },
  ];

  return (
    <div className="leave">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      <section className="leave-section" aria-labelledby="leave-balances">
        <h2 id="leave-balances">{t("balances.title")}</h2>
        {data.balances.length === 0 ? (
          <p className="field-hint">{t("balances.empty")}</p>
        ) : (
          <ul className="leave-balances">
            {data.balances.map((b) => (
              <li key={`${b.typeKey}-${b.leaveYear}`} className="leave-balance">
                <span className="leave-balance-name">
                  <bdi>{pick(b.typeNames, locale, b.typeKey)}</bdi>
                </span>
                <strong className="leave-balance-value">{t("balances.available", { value: number(b.available), unit: b.unit })}</strong>
                <span className="field-hint">{t("balances.detail", { year: String(b.leaveYear), balance: number(b.balance), pending: number(b.pending) })}</span>
                {b.source === "hr" ? (
                  <span className="field-hint">{b.asOf ? t(b.stale ? "balances.hrStale" : "balances.hr", { at: when(b.asOf) }) : t("balances.hrNone")}</span>
                ) : null}
              </li>
            ))}
          </ul>
        )}
        <ButtonLink href="/leave/team" variant="quiet" icon="people">
          {t("team.link")}
        </ButtonLink>
      </section>

      <section className="leave-section" aria-labelledby="leave-new">
        <h2 id="leave-new">{t("form.title")}</h2>
        <LeaveRequestForm
          types={data.types}
          onSubmitted={(r) => {
            setNotice(t("form.submitted", { number: r.number }));
            load();
          }}
        />
      </section>

      <section className="leave-section" aria-labelledby="leave-mine">
        <h2 id="leave-mine">{t("mine.title")}</h2>
        <DataTable caption={t("mine.caption")} columns={columns} rows={data.requests} rowKey={(r) => r.id} emptyTitle={t("mine.empty")} emptyMessage={t("mine.emptyHint")} />
      </section>

      {data.replacementAsks.length > 0 ? (
        <section className="leave-section" aria-labelledby="leave-asks">
          <h2 id="leave-asks">{t("replacement.title")}</h2>
          <DataTable caption={t("replacement.caption")} columns={askColumns} rows={data.replacementAsks} rowKey={(r) => r.id} />
        </section>
      ) : null}
    </div>
  );
}
