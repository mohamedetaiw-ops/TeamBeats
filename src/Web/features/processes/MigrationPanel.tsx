"use client";

import { useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { MIGRATION_REASONS, type InstanceSummary, type MigrationPlan, type MigrationReport, type ProcessModelSummary } from "@/lib/processes";
import "./processes.css";

/**
 * Instance migration of spec 024 R3 (FR-015, US6): an operator chooses two versions of a process, checks the proposed
 * mapping of steps, picks running instances, runs a dry run and then moves them. The API proposes the mapping, decides
 * which instances can move and why not, and checks the package and the operator's units; nothing is judged here.
 */
export function MigrationPanel({ processes }: { processes: ProcessModelSummary[] }) {
  const t = useTranslations("processes.migration");
  const locale = useLocale();
  const published = processes.filter((p) => (p.latestVersion ?? 0) > 1);
  const [process, setProcess] = useState(published[0]?.key ?? "");
  const [fromVersion, setFromVersion] = useState(published[0] ? String((published[0].latestVersion ?? 2) - 1) : "");
  const [toVersion, setToVersion] = useState(published[0] ? String(published[0].latestVersion ?? 2) : "");
  const [plan, setPlan] = useState<MigrationPlan | null>(null);
  const [mapping, setMapping] = useState<Record<string, string>>({});
  const [instances, setInstances] = useState<InstanceSummary[]>([]);
  const [selected, setSelected] = useState<string[]>([]);
  const [report, setReport] = useState<MigrationReport | null>(null);
  const [busy, setBusy] = useState<"plan" | "dry" | "move" | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  const base = `/api/v1/workflow/processes/${encodeURIComponent(process)}/migration`;

  async function failed(response: Response) {
    const list = await problemMessages(response);
    setMessages(list.length > 0 ? list : [t("failed")]);
  }

  async function prepare(event: FormEvent) {
    event.preventDefault();
    if (!process) return;
    setBusy("plan");
    setMessages([]);
    setReport(null);
    try {
      const query = new URLSearchParams({ fromVersion: fromVersion.trim(), toVersion: toVersion.trim() });
      const response = await fetch(`${base}?${query}`, { cache: "no-store" });
      if (!response.ok) {
        await failed(response);
        setPlan(null);
        return;
      }
      const next = (await response.json()) as MigrationPlan;
      setPlan(next);
      setMapping(Object.fromEntries(next.elements.filter((e) => e.target).map((e) => [e.elementId, e.target!])));
      const list = new URLSearchParams({ process, version: String(next.fromVersion), state: "active", limit: "100" });
      const found = await fetch(`/api/v1/workflow/instances?${list}`, { cache: "no-store" });
      const rows = found.ok ? ((await found.json()) as InstanceSummary[]) : [];
      setInstances(rows);
      setSelected(rows.map((r) => r.id));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  async function run(execute: boolean) {
    if (!plan || selected.length === 0) return;
    setBusy(execute ? "move" : "dry");
    setMessages([]);
    try {
      const response = await fetch(`${base}:${execute ? "execute" : "dry-run"}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ fromVersion: plan.fromVersion, toVersion: plan.toVersion, mapping, instanceIds: selected }),
      });
      if (!response.ok) {
        await failed(response);
        return;
      }
      const next = (await response.json()) as MigrationReport;
      setReport(next);
      if (execute) {
        const moved = new Set(next.instances.filter((i) => i.migratable).map((i) => i.instanceId));
        setInstances((rows) => rows.filter((r) => !moved.has(r.id)));
        setSelected((keys) => keys.filter((k) => !moved.has(k)));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  const stepName = (id: string | null) => (id ? (plan?.elements.find((e) => e.elementId === id)?.name ?? id) : "");
  const reason = (r: string | null) => ((MIGRATION_REASONS as readonly string[]).includes(r ?? "") ? t(`reasons.${r}`) : t("reasons.other"));

  return (
    <section className="processes-section" aria-labelledby="process-migration">
      <h2 id="process-migration">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {published.length === 0 ? (
        <p className="field-hint">{t("nothingToMove")}</p>
      ) : (
        <form className="processes-filters" onSubmit={prepare}>
          <SelectField
            label={t("process")}
            name="migration-process"
            value={process}
            onChange={(e) => {
              const p = published.find((x) => x.key === e.target.value);
              setProcess(e.target.value);
              setFromVersion(String((p?.latestVersion ?? 2) - 1));
              setToVersion(String(p?.latestVersion ?? 2));
              setPlan(null);
            }}
            options={published.map((p) => ({ value: p.key, label: pick(p.names, locale, p.key) }))}
          />
          <FormField label={t("fromVersion")} name="migration-from" inputMode="numeric" value={fromVersion} onChange={(e) => setFromVersion(e.target.value)} />
          <FormField label={t("toVersion")} name="migration-to" inputMode="numeric" value={toVersion} onChange={(e) => setToVersion(e.target.value)} />
          <Button type="submit" variant="secondary" busy={busy === "plan"} busyLabel={t("loading")}>
            {t("prepare")}
          </Button>
        </form>
      )}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {plan ? (
        <>
          <h3>{t("mappingTitle", { from: plan.fromVersion, to: plan.toVersion })}</h3>
          <p className="field-hint">{t("mappingHint")}</p>
          <DataTable
            caption={t("mappingTitle", { from: plan.fromVersion, to: plan.toVersion })}
            captionHidden
            rows={plan.elements}
            rowKey={(e) => e.elementId}
            emptyTitle={t("noSteps")}
            columns={[
              { key: "step", header: t("step"), rowHeader: true, cell: (e) => <bdi>{e.name}</bdi> },
              {
                key: "target",
                header: t("target"),
                cell: (e) => (
                  <SelectField
                    label={t("targetFor", { step: e.name })}
                    name={`migration-target-${e.elementId}`}
                    value={mapping[e.elementId] ?? ""}
                    onChange={(ev) =>
                      setMapping((m) => {
                        const next = { ...m };
                        if (ev.target.value) next[e.elementId] = ev.target.value;
                        else delete next[e.elementId];
                        return next;
                      })
                    }
                    options={[
                      { value: "", label: t("notMapped") },
                      ...plan.targetElements.filter((x) => x.type === e.type).map((x) => ({ value: x.elementId, label: x.name })),
                    ]}
                  />
                ),
              },
            ]}
          />
          <h3>{t("instancesTitle", { count: instances.length })}</h3>
          <DataTable
            caption={t("instancesTitle", { count: instances.length })}
            captionHidden
            rows={instances}
            rowKey={(i) => i.id}
            rowLabel={(i) => i.businessKey}
            selected={selected}
            onSelectedChange={setSelected}
            emptyTitle={t("noInstances")}
            columns={[{ key: "businessKey", header: t("businessKey"), rowHeader: true, cell: (i) => <bdi>{i.businessKey}</bdi> }]}
          />
          <div className="processes-filters">
            <Button variant="secondary" onClick={() => run(false)} disabled={selected.length === 0} busy={busy === "dry"} busyLabel={t("checking")}>
              {t("dryRun")}
            </Button>
            <Button variant="primary" onClick={() => run(true)} disabled={selected.length === 0} busy={busy === "move"} busyLabel={t("moving")}>
              {t("move", { count: selected.length })}
            </Button>
          </div>
        </>
      ) : null}
      {report ? (
        <>
          <Alert tone={report.refused === 0 ? "success" : "warning"} title={t(report.executed ? "movedTitle" : "checkedTitle")} live>
            {t(report.executed ? "moved" : "checked", { migratable: report.migratable, refused: report.refused })}
          </Alert>
          <DataTable
            caption={t("reportTitle")}
            rows={report.instances}
            rowKey={(i) => i.instanceId}
            emptyTitle={t("noInstances")}
            columns={[
              { key: "businessKey", header: t("businessKey"), rowHeader: true, cell: (i) => <bdi>{i.businessKey ?? i.instanceId}</bdi> },
              {
                key: "result",
                header: t("result"),
                cell: (i) => <Badge tone={i.migratable ? "success" : "warning"}>{t(i.migratable ? (report.executed ? "resultMoved" : "resultCanMove") : "resultCannotMove")}</Badge>,
              },
              {
                key: "reason",
                header: t("reason"),
                cell: (i) => (i.migratable ? "" : <bdi>{i.elementId ? t("reasonAt", { reason: reason(i.reason), step: stepName(i.elementId) }) : reason(i.reason)}</bdi>),
              },
            ]}
          />
        </>
      ) : null}
    </section>
  );
}
