"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { Drawer } from "@/components/ui/Dialog";
import { FormField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { FactList } from "@/components/ui/Patterns";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { PersonPicker } from "@/features/services/PersonPicker";
import type { AuditEvent, AuditExport, AuditIntegrity, AuditPage, AuditRetention } from "@/lib/admin/types";
import { ResultAlert, shortId, useAdminAction, usePeopleNames, useWhen } from "./shared";

const RETURN = "/admin/audit";

type Filters = { actor: string | null; action: string; targetType: string; from: string; to: string };
const EMPTY: Filters = { actor: null, action: "", targetType: "", from: "", to: "" };

function query(filters: Filters, cursor?: string | null): string {
  const q = new URLSearchParams({ limit: "50" });
  if (filters.actor) q.set("actor", filters.actor);
  if (filters.action.trim()) q.set("action", filters.action.trim());
  if (filters.targetType.trim()) q.set("targetType", filters.targetType.trim());
  if (filters.from) q.set("from", new Date(`${filters.from}T00:00:00`).toISOString());
  if (filters.to) q.set("to", new Date(`${filters.to}T23:59:59`).toISOString());
  if (cursor) q.set("cursor", cursor);
  return q.toString();
}

/**
 * Audit log (spec 006 US1 to US3): who did what and when, limited by the API to the administrator's scope, with each
 * entry's recorded changes, a check that the sealed chain is intact (ADR-0008), the retention period, and an export
 * that runs in the background and is itself audited. Exporting asks for a fresh sign-in.
 */
export function AuditAdmin({
  initial,
  retention: initialRetention,
  initialAction = "",
}: {
  initial: AuditPage;
  retention: AuditRetention | null;
  initialAction?: string;
}) {
  const t = useTranslations("admin.audit");
  const when = useWhen();
  const { busy, result, run } = useAdminAction(RETURN);
  const [page, setPage] = useState(initial);
  const [filters, setFilters] = useState<Filters>({ ...EMPTY, action: initialAction });
  const [applied, setApplied] = useState<Filters>({ ...EMPTY, action: initialAction });
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState<AuditEvent | null>(null);
  const [integrity, setIntegrity] = useState<AuditIntegrity | null>(null);
  const [retention, setRetention] = useState(initialRetention);
  const [days, setDays] = useState<number | null>(initialRetention?.days ?? null);
  const [format, setFormat] = useState("csv");
  const [job, setJob] = useState<AuditExport | null>(null);
  const person = usePeopleNames(page.items.map((e) => e.actorId));

  async function load(next: Filters, cursor?: string | null) {
    setLoading(true);
    setFailed(false);
    try {
      const response = await fetch(`/api/v1/audit/events?${query(next, cursor)}`, { cache: "no-store" });
      if (!response.ok) {
        setFailed(true);
        return;
      }
      const data = (await response.json()) as AuditPage;
      setPage(cursor ? { items: [...page.items, ...data.items], nextCursor: data.nextCursor } : data);
      setApplied(next);
    } catch {
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }

  // An export runs on the worker; the screen asks for its state until it is done.
  useEffect(() => {
    if (!job || job.status === "completed" || job.status === "failed") return;
    const timer = window.setTimeout(async () => {
      const response = await fetch(`/api/v1/audit/exports/${job.id}`, { cache: "no-store" });
      if (response.ok) setJob((await response.json()) as AuditExport);
    }, 2000);
    return () => window.clearTimeout(timer);
  }, [job]);

  const actor = (e: AuditEvent) => (e.actorId ? person(e.actorId) : t.has(`actors.${e.actorType}`) ? t(`actors.${e.actorType}`) : e.actorType);
  const outcomeTone = (o: string) => (o === "success" ? "success" : o === "denied" ? "warning" : "danger");
  const filtered = JSON.stringify(applied) !== JSON.stringify(EMPTY);

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />

      <section className="admin-panel" aria-labelledby="audit-events-title">
        <h2 id="audit-events-title">{t("events")}</h2>
        <form
          className="admin-filters"
          role="search"
          aria-label={t("filters")}
          onSubmit={(e) => {
            e.preventDefault();
            void load(filters);
          }}
        >
          <div className="admin-filters-wide">
            <PersonPicker label={t("actor")} value={filters.actor} onChange={(actor) => setFilters({ ...filters, actor })} />
          </div>
          <FormField label={t("action")} hint={t("actionHint")} name="action" dir="ltr" value={filters.action} onChange={(e) => setFilters({ ...filters, action: e.target.value })} />
          <FormField label={t("targetType")} hint={t("targetTypeHint")} name="targetType" dir="ltr" value={filters.targetType} onChange={(e) => setFilters({ ...filters, targetType: e.target.value })} />
          <FormField label={t("from")} name="from" type="date" value={filters.from} onChange={(e) => setFilters({ ...filters, from: e.target.value })} />
          <FormField label={t("to")} name="to" type="date" value={filters.to} onChange={(e) => setFilters({ ...filters, to: e.target.value })} />
          <div className="admin-row">
            <Button type="submit" variant="primary" icon="filter" busy={loading} busyLabel={t("loading")}>
              {t("apply")}
            </Button>
            {filtered ? (
              <Button
                variant="quiet"
                onClick={() => {
                  setFilters(EMPTY);
                  void load(EMPTY);
                }}
              >
                {t("clear")}
              </Button>
            ) : null}
          </div>
        </form>
        {failed ? <Alert tone="danger" title={t("loadFailed")} live /> : null}
        <DataTable
          caption={t("events")}
          rows={page.items}
          rowKey={(e) => e.id}
          emptyTitle={t("empty")}
          emptyMessage={filtered ? t("emptyFiltered") : t("emptyHint")}
          density="compact"
          columns={[
            { key: "when", header: t("when"), rowHeader: true, cell: (e) => <time dateTime={e.occurredAt}>{when(e.occurredAt, "short")}</time> },
            { key: "actor", header: t("who"), cell: (e) => <bdi>{actor(e)}</bdi> },
            { key: "action", header: t("action"), cell: (e) => <code dir="ltr">{e.action}</code> },
            {
              key: "target",
              header: t("target"),
              cell: (e) => (
                <span dir="ltr">
                  <code>{e.targetType}</code> {e.targetId ? <span className="admin-meta">{shortId(e.targetId)}</span> : null}
                </span>
              ),
              hideable: true,
            },
            { key: "outcome", header: t("outcome"), cell: (e) => <Badge tone={outcomeTone(e.outcome)}>{t.has(`outcomes.${e.outcome}`) ? t(`outcomes.${e.outcome}`) : e.outcome}</Badge> },
            {
              key: "details",
              header: t("details"),
              cell: (e) => (
                <Button size="xs" variant="quiet" onClick={() => setOpen(e)}>
                  {t("view")}
                </Button>
              ),
            },
          ]}
          footer={
            page.nextCursor ? (
              <Button busy={loading} busyLabel={t("loading")} onClick={() => void load(applied, page.nextCursor)}>
                {t("more")}
              </Button>
            ) : null
          }
        />
      </section>

      <div className="admin-grid">
        <section className="admin-panel" aria-labelledby="audit-integrity-title">
          <h2 id="audit-integrity-title">{t("integrity.title")}</h2>
          <p className="admin-meta">{t("integrity.intro")}</p>
          {integrity ? (
            <Alert tone={integrity.intact ? "success" : "danger"} title={t(integrity.intact ? "integrity.intact" : "integrity.broken")} live>
              <p>{t("integrity.detail", { checked: integrity.checked, sealed: integrity.sealedThrough, unsealed: integrity.unsealed })}</p>
              {integrity.firstBreak !== null ? <p>{t("integrity.firstBreak", { position: integrity.firstBreak })}</p> : null}
            </Alert>
          ) : null}
          <Button
            icon="check"
            busy={busy === "integrity"}
            busyLabel={t("loading")}
            onClick={async () => {
              const outcome = await run<AuditIntegrity>("integrity", "GET", "/api/v1/audit/integrity", undefined, t("integrity.checkedMessage"));
              if (outcome.ok) setIntegrity(outcome.data);
            }}
          >
            {t("integrity.check")}
          </Button>
        </section>

        {retention ? (
          <section className="admin-panel" aria-labelledby="audit-retention-title">
            <h2 id="audit-retention-title">{t("retention.title")}</h2>
            <p className="admin-meta">{t("retention.intro", { minimum: retention.minimumDays })}</p>
            <form
              className="admin-form"
              onSubmit={async (e) => {
                e.preventDefault();
                const outcome = await run<AuditRetention>("retention", "PUT", "/api/v1/audit/retention", { days }, t("retention.saved"));
                if (outcome.ok) setRetention(outcome.data);
              }}
            >
              <NumberField label={t("retention.days")} name="days" min={retention.minimumDays} max={3650} unit={t("retention.unit")} value={days} onValueChange={setDays} />
              <div className="admin-row">
                <Button type="submit" busy={busy === "retention"} busyLabel={t("loading")} disabled={days === retention.days || days === null}>
                  {t("retention.save")}
                </Button>
              </div>
            </form>
          </section>
        ) : null}

        <section className="admin-panel" aria-labelledby="audit-export-title">
          <h2 id="audit-export-title">{t("export.title")}</h2>
          <p className="admin-meta">{t("export.intro")}</p>
          <RadioGroup
            legend={t("export.format")}
            name="format"
            value={format}
            onChange={(e) => setFormat(e.target.value)}
            options={[
              { value: "csv", label: t("export.csv") },
              { value: "json", label: t("export.json") },
            ]}
          />
          <div className="admin-row">
            <Button
              icon="download"
              busy={busy === "export"}
              busyLabel={t("loading")}
              onClick={async () => {
                const body = {
                  format,
                  actor: applied.actor,
                  action: applied.action.trim() || null,
                  targetType: applied.targetType.trim() || null,
                  from: applied.from ? new Date(`${applied.from}T00:00:00`).toISOString() : null,
                  to: applied.to ? new Date(`${applied.to}T23:59:59`).toISOString() : null,
                };
                const outcome = await run<{ id: string; status: string }>("export", "POST", "/api/v1/audit/exports", body, t("export.started"));
                if (outcome.ok) setJob({ id: outcome.data.id, format, status: outcome.data.status, rowCount: null, createdAt: new Date().toISOString(), completedAt: null, expiresAt: null });
              }}
            >
              {t(filtered ? "export.startFiltered" : "export.start")}
            </Button>
          </div>
          {job ? (
            <div className="admin-export" role="status">
              <Badge tone={job.status === "completed" ? "success" : job.status === "failed" ? "danger" : "info"}>
                {t.has(`export.statuses.${job.status}`) ? t(`export.statuses.${job.status}`) : job.status}
              </Badge>
              {job.status === "completed" ? (
                <>
                  <span className="admin-meta">{t("export.rows", { count: job.rowCount ?? 0, expires: when(job.expiresAt) })}</span>
                  <ButtonLink variant="primary" size="sm" icon="download" href={`/api/v1/audit/exports/${job.id}/file`} download>
                    {t("export.download")}
                  </ButtonLink>
                </>
              ) : null}
            </div>
          ) : null}
          <p className="admin-meta">{t("stepUpHint")}</p>
        </section>
      </div>

      <Drawer open={open !== null} onClose={() => setOpen(null)} title={t("entry")} closeLabel={t("close")}>
        {open ? (
          <>
            <FactList
              facts={[
                { key: "when", label: t("when"), value: when(open.occurredAt) },
                { key: "who", label: t("who"), value: <bdi>{actor(open)}</bdi> },
                { key: "action", label: t("action"), value: <code dir="ltr">{open.action}</code> },
                {
                  key: "target",
                  label: t("target"),
                  value: (
                    <span dir="ltr">
                      <code>{open.targetType}</code> {open.targetId ? <code>{open.targetId}</code> : null}
                    </span>
                  ),
                },
                { key: "outcome", label: t("outcome"), value: t.has(`outcomes.${open.outcome}`) ? t(`outcomes.${open.outcome}`) : open.outcome },
                { key: "position", label: t("position"), value: open.chainPosition ?? t("unsealed") },
                { key: "correlation", label: t("correlation"), value: open.correlationId ? <code dir="ltr">{open.correlationId}</code> : "" },
              ]}
            />
            <h3>{t("changes")}</h3>
            {open.changes ? (
              <pre className="admin-json" dir="ltr" tabIndex={0}>
                {JSON.stringify(open.changes, null, 2)}
              </pre>
            ) : (
              <p className="admin-meta">{t("noChanges")}</p>
            )}
          </>
        ) : null}
      </Drawer>
    </div>
  );
}
