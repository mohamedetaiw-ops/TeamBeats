"use client";

import { useState, type FormEvent } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { StatCards } from "@/components/ui/Blocks";
import { Button } from "@/components/ui/Button";
import { BarList } from "@/components/ui/Charts";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { durationParts, type ProcessModelSummary, type ProcessMonitoring } from "@/lib/processes";
import "./processes.css";

/**
 * Monitoring of spec 024 US5 scenarios 3 and 4 (FR-014): for one version of a process and a period, instance counts,
 * durations, and per step the instances waiting there now, open incidents and how long the step took. Numbers only:
 * no variables and no people. The API decides who may see them (process analysts and operators at the owner's unit).
 */
export function MonitoringPanel({ processes }: { processes: ProcessModelSummary[] }) {
  const t = useTranslations("processes.monitoring");
  const locale = useLocale();
  const format = useFormatter();
  const published = processes.filter((p) => p.latestVersion != null);
  const [process, setProcess] = useState(published[0]?.key ?? "");
  const [version, setVersion] = useState("");
  const [days, setDays] = useState("30");
  const [data, setData] = useState<ProcessMonitoring | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  const duration = (seconds: number | null) => {
    if (seconds == null) return t("noDuration");
    const { unit, value } = durationParts(seconds);
    return t(`durations.${unit}`, { value });
  };

  async function load(event: FormEvent) {
    event.preventDefault();
    if (!process) return;
    setBusy(true);
    setMessages([]);
    const query = new URLSearchParams();
    if (version.trim()) query.set("version", version.trim());
    const span = Math.min(366, Math.max(1, Number(days) || 30));
    const to = new Date();
    query.set("to", to.toISOString());
    query.set("from", new Date(to.getTime() - span * 86400000).toISOString());
    try {
      const response = await fetch(`/api/v1/workflow/processes/${encodeURIComponent(process)}/monitoring?${query}`, { cache: "no-store" });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        setData(null);
        return;
      }
      setData((await response.json()) as ProcessMonitoring);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const name = (id: string) => data?.elements.find((e) => e.elementId === id)?.name ?? id;

  return (
    <section className="processes-section" aria-labelledby="process-monitoring">
      <h2 id="process-monitoring">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {published.length === 0 ? (
        <p className="field-hint">{t("nothingPublished")}</p>
      ) : (
        <form className="processes-filters" onSubmit={load}>
          <SelectField
            label={t("process")}
            name="process"
            value={process}
            onChange={(e) => setProcess(e.target.value)}
            options={published.map((p) => ({ value: p.key, label: pick(p.names, locale, p.key) }))}
          />
          <FormField label={t("version")} hint={t("versionHint")} name="version" inputMode="numeric" value={version} onChange={(e) => setVersion(e.target.value)} />
          <FormField label={t("days")} hint={t("daysHint")} name="days" inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} />
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("loading")}>
            {t("show")}
          </Button>
        </form>
      )}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {data ? (
        <>
          <p className="field-hint">
            {t("period", {
              version: data.version,
              from: format.dateTime(new Date(data.from), { dateStyle: "medium" }),
              to: format.dateTime(new Date(data.to), { dateStyle: "medium" }),
            })}
          </p>
          <StatCards
            label={t("counts")}
            stats={[
              { key: "active", label: t("active"), value: format.number(data.active) },
              { key: "completed", label: t("completed"), value: format.number(data.completed) },
              { key: "cancelled", label: t("cancelled"), value: format.number(data.cancelled) },
              { key: "withIncidents", label: t("withIncidents"), value: format.number(data.withIncidents) },
              { key: "average", label: t("average"), value: duration(data.averageSeconds) },
              { key: "p90", label: t("p90"), value: duration(data.p90Seconds) },
            ]}
          />
          <h3>{t("slowest")}</h3>
          {data.slowest.length === 0 ? (
            <p className="field-hint">{t("noSlowest")}</p>
          ) : (
            <BarList
              label={t("slowest")}
              items={data.slowest.map((id) => {
                const element = data.elements.find((e) => e.elementId === id)!;
                return { key: id, label: name(id), value: element.p90Seconds ?? 0, valueText: duration(element.p90Seconds) };
              })}
            />
          )}
          <DataTable
            caption={t("elements")}
            rows={data.elements}
            rowKey={(e) => e.elementId}
            emptyTitle={t("noElements")}
            columns={[
              { key: "name", header: t("step"), rowHeader: true, cell: (e) => <bdi>{e.name}</bdi> },
              { key: "active", header: t("waitingNow"), align: "end", sortValue: (e) => e.active, cell: (e) => format.number(e.active) },
              { key: "incidents", header: t("openIncidents"), align: "end", sortValue: (e) => e.incidents, cell: (e) => format.number(e.incidents) },
              { key: "finished", header: t("finished"), align: "end", sortValue: (e) => e.finished, cell: (e) => format.number(e.finished) },
              { key: "average", header: t("average"), align: "end", sortValue: (e) => e.averageSeconds ?? -1, cell: (e) => duration(e.averageSeconds) },
              { key: "p90", header: t("p90"), align: "end", sortValue: (e) => e.p90Seconds ?? -1, cell: (e) => duration(e.p90Seconds) },
            ]}
          />
        </>
      ) : null}
    </section>
  );
}
