"use client";

import { useState, type FormEvent } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { Drawer } from "@/components/ui/Dialog";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import {
  INCIDENT_RESOLUTIONS,
  INSTANCE_STATES,
  type Incident,
  type IncidentResolution,
  type InstanceDetail,
  type InstanceSummary,
  type ProcessModelSummary,
} from "@/lib/processes";
import "./processes.css";

const stateTone: Record<string, BadgeTone> = { active: "info", completed: "success", cancelled: "neutral" };

/**
 * The operator view of spec 024 US5 (FR-013): search process instances, open one to read its variables, waiting
 * steps, incidents and history, change variables while an incident is open, retry, skip or cancel at an incident and
 * cancel an instance. Every action needs a reason. The API lists only instances at the units where the person operates
 * and checks every action again.
 */
export function OperationsPanel({ processes }: { processes: ProcessModelSummary[] }) {
  const t = useTranslations("processes.operations");
  const locale = useLocale();
  const format = useFormatter();
  const [process, setProcess] = useState("");
  const [state, setState] = useState("");
  const [businessKey, setBusinessKey] = useState("");
  const [withIncident, setWithIncident] = useState(false);
  const [rows, setRows] = useState<InstanceSummary[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [detail, setDetail] = useState<InstanceDetail | null>(null);

  const when = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });
  const processName = (key: string) => pick(processes.find((p) => p.key === key)?.names, locale, key);

  async function search(event?: FormEvent) {
    event?.preventDefault();
    setBusy(true);
    setMessages([]);
    const query = new URLSearchParams();
    if (process) query.set("process", process);
    if (state) query.set("state", state);
    if (businessKey.trim()) query.set("businessKey", businessKey.trim());
    if (withIncident) query.set("withIncident", "true");
    try {
      const response = await fetch(`/api/v1/workflow/instances?${query}`, { cache: "no-store" });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      setRows((await response.json()) as InstanceSummary[]);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function open(id: string) {
    setMessages([]);
    const response = await fetch(`/api/v1/workflow/instances/${id}`, { cache: "no-store" });
    if (response.ok) setDetail((await response.json()) as InstanceDetail);
    else setMessages([t("failed")]);
  }

  return (
    <section className="processes-section" aria-labelledby="process-operations">
      <h2 id="process-operations">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      <form className="processes-filters" onSubmit={search}>
        <SelectField
          label={t("process")}
          name="process"
          value={process}
          onChange={(e) => setProcess(e.target.value)}
          options={[{ value: "", label: t("anyProcess") }, ...processes.map((p) => ({ value: p.key, label: pick(p.names, locale, p.key) }))]}
        />
        <SelectField
          label={t("state")}
          name="state"
          value={state}
          onChange={(e) => setState(e.target.value)}
          options={[{ value: "", label: t("anyState") }, ...INSTANCE_STATES.map((s) => ({ value: s, label: t(`states.${s}`) }))]}
        />
        <FormField label={t("businessKey")} name="businessKey" value={businessKey} onChange={(e) => setBusinessKey(e.target.value)} maxLength={200} />
        <Checkbox label={t("withIncident")} checked={withIncident} onChange={(e) => setWithIncident(e.target.checked)} />
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("searching")}>
          {t("search")}
        </Button>
      </form>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {rows ? (
        <DataTable
          caption={t("results")}
          rows={rows}
          rowKey={(r) => r.id}
          emptyTitle={t("emptyTitle")}
          emptyMessage={t("empty")}
          columns={[
            {
              key: "businessKey",
              header: t("businessKey"),
              rowHeader: true,
              cell: (r) => (
                <Button size="sm" variant="quiet" onClick={() => open(r.id)}>
                  <bdi>{r.businessKey}</bdi>
                </Button>
              ),
            },
            { key: "process", header: t("process"), cell: (r) => <bdi>{t("processVersion", { name: processName(r.process), version: r.version })}</bdi> },
            { key: "status", header: t("state"), cell: (r) => <Badge tone={stateTone[r.status] ?? "neutral"}>{t(`states.${r.status}`)}</Badge> },
            {
              key: "incidents",
              header: t("incidents"),
              align: "end",
              sortValue: (r) => r.openIncidents,
              cell: (r) => (r.openIncidents > 0 ? <Badge tone="danger">{t("openIncidents", { count: r.openIncidents })}</Badge> : "0"),
            },
            { key: "started", header: t("started"), sortValue: (r) => r.startedAt, cell: (r) => when(r.startedAt), priority: "low" },
          ]}
        />
      ) : null}
      {detail ? (
        <InstanceDrawer
          detail={detail}
          title={t("instanceTitle", { key: detail.instance.businessKey, name: processName(detail.instance.process) })}
          onChange={setDetail}
          onClose={() => {
            setDetail(null);
            if (rows) void search();
          }}
        />
      ) : null}
    </section>
  );
}

function InstanceDrawer({
  detail,
  title,
  onChange,
  onClose,
}: {
  detail: InstanceDetail;
  title: string;
  onChange: (detail: InstanceDetail) => void;
  onClose: () => void;
}) {
  const t = useTranslations("processes.operations");
  const format = useFormatter();
  const when = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });
  const [variables, setVariables] = useState(() => JSON.stringify(detail.variables, null, 2));
  const [reason, setReason] = useState("");
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const open = detail.incidents.filter((i) => i.status === "open");
  const active = detail.instance.status === "active";

  async function act(name: string, url: string, method: string, body: unknown, done: string) {
    setMessages([]);
    setNotice(null);
    if (!reason.trim()) {
      setMessages([t("reasonNeeded")]);
      return;
    }
    setBusy(name);
    try {
      const response = await fetch(url, { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      const fresh = await fetch(`/api/v1/workflow/instances/${detail.instance.id}`, { cache: "no-store" });
      if (fresh.ok) {
        const next = (await fresh.json()) as InstanceDetail;
        onChange(next);
        setVariables(JSON.stringify(next.variables, null, 2));
      }
      setReason("");
      setNotice(done);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function saveVariables() {
    let parsed: unknown;
    try {
      parsed = JSON.parse(variables);
    } catch {
      setMessages([t("variablesInvalid")]);
      return;
    }
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      setMessages([t("variablesInvalid")]);
      return;
    }
    const before = detail.variables;
    const after = parsed as Record<string, unknown>;
    const changes: Record<string, unknown> = {};
    for (const key of new Set([...Object.keys(before), ...Object.keys(after)])) {
      if (JSON.stringify(before[key]) !== JSON.stringify(after[key])) changes[key] = key in after ? after[key] : null;
    }
    if (Object.keys(changes).length === 0) {
      setMessages([t("variablesUnchanged")]);
      return;
    }
    void act("variables", `/api/v1/workflow/instances/${detail.instance.id}/variables`, "PUT", { variables: changes, reason }, t("variablesSaved"));
  }

  const resolve = (incident: Incident, resolution: IncidentResolution) =>
    act(`${incident.id}-${resolution}`, `/api/v1/workflow/incidents/${incident.id}:resolve`, "POST", { resolution, reason }, t(`resolved.${resolution}`));

  return (
    <Drawer open onClose={onClose} title={title} description={t(`states.${detail.instance.status}`)} size="lg" closeLabel={t("close")}>
      <div className="processes-drawer">
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

        <h3>{t("waiting")}</h3>
        {detail.active.length === 0 ? (
          <p className="field-hint">{t("nothingWaiting")}</p>
        ) : (
          <ul className="processes-list">
            {detail.active.map((a, i) => (
              <li key={`${a.elementId}-${i}`}>
                <bdi>{a.name}</bdi> <Badge tone={a.status === "incident" ? "danger" : "info"}>{t(`tokenStates.${a.status}`)}</Badge>
              </li>
            ))}
          </ul>
        )}

        <h3>{t("incidentsTitle")}</h3>
        {detail.incidents.length === 0 ? <p className="field-hint">{t("noIncidents")}</p> : null}
        <ul className="processes-list">
          {detail.incidents.map((i) => (
            <li key={i.id} className="processes-incident">
              <span>
                <bdi>{i.stepName}</bdi> <Badge tone={i.status === "open" ? "danger" : "neutral"}>{t(`incidentStates.${i.status}`)}</Badge>{" "}
                <span className="field-hint">
                  {t("incidentFacts", { error: i.errorCode ? `${i.errorClass}: ${i.errorCode}` : i.errorClass, when: when(i.raisedAt) })}
                </span>
              </span>
              {i.status === "open" ? (
                <span className="processes-actions">
                  {INCIDENT_RESOLUTIONS.map((r) => (
                    <Button
                      key={r}
                      size="sm"
                      variant={r === "cancelled" ? "destructive" : "secondary"}
                      busy={busy === `${i.id}-${r}`}
                      busyLabel={t("working")}
                      onClick={() => resolve(i, r)}
                    >
                      {t(`resolve.${r}`)}
                    </Button>
                  ))}
                </span>
              ) : i.reason ? (
                <span className="field-hint">
                  {t(`resolved.${i.resolution as IncidentResolution}`)} <bdi>{i.reason}</bdi>
                </span>
              ) : null}
            </li>
          ))}
        </ul>

        {active ? (
          <TextAreaField
            label={t("reason")}
            name="reason"
            hint={t("reasonHint")}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            maxLength={500}
            rows={2}
          />
        ) : null}

        <h3>{t("variables")}</h3>
        <TextAreaField
          label={t("variablesLabel")}
          name="variables"
          hint={open.length > 0 ? t("variablesHint") : t("variablesReadOnly")}
          value={variables}
          onChange={(e) => setVariables(e.target.value)}
          readOnly={open.length === 0}
          rows={8}
          dir="ltr"
          className="processes-code"
        />
        {open.length > 0 ? (
          <Button onClick={saveVariables} busy={busy === "variables"} busyLabel={t("working")}>
            {t("saveVariables")}
          </Button>
        ) : null}

        <h3>{t("tasks")}</h3>
        {detail.tasks.length === 0 ? <p className="field-hint">{t("noTasks")}</p> : null}
        <ul className="processes-list">
          {detail.tasks.map((task) => (
            <li key={task.id}>
              <bdi>{task.name}</bdi> <Badge>{t(`taskStates.${task.status}`)}</Badge> <span className="field-hint">{when(task.createdAt)}</span>
            </li>
          ))}
        </ul>

        <h3>{t("history")}</h3>
        <ol className="processes-list">
          {detail.history.map((h, i) => (
            <li key={i}>
              <span className="field-hint">{when(h.at)}</span> <bdi>{h.elementId || t("instanceLevel")}</bdi> <code>{h.event}</code>
            </li>
          ))}
        </ol>

        {active ? (
          <Button
            variant="destructive"
            busy={busy === "cancel"}
            busyLabel={t("working")}
            onClick={() => act("cancel", `/api/v1/workflow/instances/${detail.instance.id}:cancel`, "POST", { reason }, t("cancelled"))}
          >
            {t("cancelInstance")}
          </Button>
        ) : null}
      </div>
    </Drawer>
  );
}
