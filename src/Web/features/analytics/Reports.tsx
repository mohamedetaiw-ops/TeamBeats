"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import {
  reportFileHref,
  reportHref,
  schedulesApi,
  type ReportCell,
  type ReportList,
  type ReportSchedule,
  type ReportStatus,
  type ReportSummary,
  type ReportTable,
  type ReportView,
} from "@/lib/analytics/reports";
import { problemMessages } from "@/lib/notifications/types";
import { DataTable } from "@/components/ui/DataTable";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { ButtonLink } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { StateView } from "@/components/ui/StateView";
import { Alert } from "@/components/ui/Alert";
import Link from "next/link";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

const tones: Record<ReportStatus, BadgeTone> = { queued: "info", ready: "success", skipped: "warning", failed: "danger" };

function useLabels() {
  const t = useTranslations("analytics.reports");
  const format = useFormatter();
  return {
    t,
    status: (s: ReportStatus) => <Badge tone={tones[s]}>{t(`status.${s}`)}</Badge>,
    dashboard: (d: string) => (t.has(`dashboard.${d}`) ? t(`dashboard.${d}`) : d),
    day: (iso: string | null) => (iso ? format.dateTime(new Date(`${iso.slice(0, 10)}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) : ""),
    time: (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : ""),
  };
}

/** The person's reports and their schedules (spec 021 US7). Reports are kept 90 days; only their recipient sees them. */
export function ReportsList({ list }: { list: ReportList }) {
  const { t, status, dashboard, day, time } = useLabels();
  const locale = useLocale();
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const period = (r: ReportSummary) => (r.periodFrom && r.periodTo ? t("period", { from: day(r.periodFrom), to: day(r.periodTo) }) : t("periodDashboard"));
  const recipients = (s: ReportSchedule) => (s.recipientUnitNames ? t("recipientsUnit", { name: pick(s.recipientUnitNames, locale) }) : t("recipientsMe"));

  async function remove(id: string) {
    setError(null);
    const response = await fetch(`${schedulesApi}/${encodeURIComponent(id)}`, { method: "DELETE" });
    if (!response.ok) {
      setError((await problemMessages(response))[0] ?? t("failed"));
      return;
    }
    router.refresh();
  }

  return (
    <div className="analytics-reports">
      <section aria-labelledby="reports-received">
        <h2 id="reports-received">{t("received")}</h2>
        <DataTable
          caption={t("received")}
          rows={list.reports}
          rowKey={(r) => r.id}
          emptyTitle={t("empty")}
          emptyMessage={list.canExport ? t("emptyHintExport") : t("emptyHint")}
          columns={[
            {
              key: "name",
              header: t("column.name"),
              rowHeader: true,
              cell: (r) => <Link href={reportHref(r.id)}>{r.name}</Link>,
              sortValue: (r) => r.name,
            },
            { key: "dashboard", header: t("column.dashboard"), cell: (r) => dashboard(r.dashboard) },
            { key: "kind", header: t("column.kind"), cell: (r) => (r.scheduleId ? t("kindScheduled") : t("kindExport")), priority: "low" },
            { key: "period", header: t("column.period"), cell: period, priority: "low" },
            { key: "status", header: t("column.status"), cell: (r) => status(r.status) },
            { key: "created", header: t("column.created"), cell: (r) => time(r.createdAt), sortValue: (r) => r.createdAt },
          ]}
          defaultSort={{ key: "created", direction: "descending" }}
        />
      </section>
      {list.canSchedule ? (
        <section aria-labelledby="reports-schedules">
          <h2 id="reports-schedules">{t("schedules")}</h2>
          {error ? <Alert tone="danger" title={error} live /> : null}
          <DataTable
            caption={t("schedules")}
            rows={list.schedules}
            rowKey={(s) => s.id}
            emptyTitle={t("noSchedules")}
            emptyMessage={t("noSchedulesHint")}
            columns={[
              { key: "name", header: t("column.name"), rowHeader: true, cell: (s) => s.name },
              { key: "dashboard", header: t("column.dashboard"), cell: (s) => dashboard(s.definition.dashboard) },
              { key: "cadence", header: t("column.cadence"), cell: (s) => (s.cadence === "weekly" ? t("cadenceWeekly") : t("cadenceMonthly")) },
              { key: "recipients", header: t("column.recipients"), cell: recipients },
              { key: "next", header: t("column.next"), cell: (s) => time(s.nextRunAt), priority: "low" },
              {
                key: "actions",
                header: t("column.actions"),
                cell: (s) => (
                  <ConfirmAction
                    label={t("remove", { name: s.name })}
                    question={t("removeQuestion", { name: s.name })}
                    confirmLabel={t("removeConfirm")}
                    cancelLabel={t("cancel")}
                    onConfirm={() => remove(s.id)}
                  />
                ),
              },
            ]}
          />
        </section>
      ) : null}
    </div>
  );
}

/** One report: its status, the files when the package has exports, and every table of the dashboard's answer. */
export function ReportDetail({ view }: { view: ReportView }) {
  const { t, status, dashboard, day, time } = useLabels();
  const format = useFormatter();
  const r = view.report;
  const label = (path: string) =>
    path
      .split(".")
      .map((segment) => (t.has(`column.${segment}`) ? t(`column.${segment}`) : segment))
      .join(" · ");
  const cell = (value: ReportCell) =>
    value === null ? t("hidden") : typeof value === "number" ? format.number(value) : typeof value === "boolean" ? t(value ? "yes" : "no") : value;

  return (
    <div className="analytics-report" data-testid="analytics-report">
      <dl className="analytics-stats">
        <div>
          <dt>{t("column.dashboard")}</dt>
          <dd>{dashboard(r.dashboard)}</dd>
        </div>
        <div>
          <dt>{t("column.status")}</dt>
          <dd>{status(r.status)}</dd>
        </div>
        <div>
          <dt>{t("column.period")}</dt>
          <dd>{r.periodFrom && r.periodTo ? t("period", { from: day(r.periodFrom), to: day(r.periodTo) }) : t("periodDashboard")}</dd>
        </div>
        <div>
          <dt>{t("column.created")}</dt>
          <dd>{time(r.doneAt ?? r.createdAt)}</dd>
        </div>
      </dl>
      {view.canDownload ? (
        <div className="analytics-report-files">
          <ButtonLink href={reportFileHref(r.id, "xlsx")} icon="download" size="sm" download>
            {t("downloadXlsx")}
          </ButtonLink>
          <ButtonLink href={reportFileHref(r.id, "csv")} icon="download" size="sm" download>
            {t("downloadCsv")}
          </ButtonLink>
        </div>
      ) : null}
      {r.status === "queued" ? (
        <StateView kind="loading" title={t("queuedTitle")} message={t("queuedHint")} />
      ) : r.status === "skipped" ? (
        <StateView kind="denied" title={t("skippedTitle")} message={t("skippedHint")} />
      ) : r.status === "failed" ? (
        <StateView kind="error" title={t("failedTitle")} message={t("failedHint")} />
      ) : (
        view.tables.map((table: ReportTable) => (
          <section key={table.key} aria-label={label(table.key)} data-testid={`report-table-${table.key}`}>
            <h2>{label(table.key)}</h2>
            <DataTable
              caption={label(table.key)}
              density="compact"
              rows={table.rows.map((row, index) => ({ index, row }))}
              rowKey={(x) => String(x.index)}
              columns={table.columns.map((column, c) => ({
                key: column,
                header: label(column),
                rowHeader: c === 0,
                align: table.rows.some((row) => typeof row[c] === "number") ? ("end" as const) : ("start" as const),
                cell: (x: { index: number; row: ReportCell[] }) => cell(x.row[c] ?? null),
              }))}
            />
          </section>
        ))
      )}
    </div>
  );
}
