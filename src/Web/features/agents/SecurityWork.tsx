"use client";

import { useId, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button, ButtonLink } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { SEVERITIES, type AgentAlert, type AgentIncident } from "@/lib/agents/types";

type Result = { ok: boolean; messages: string[] } | null;

function Messages({ result }: { result: Result }) {
  if (!result) return null;
  return (
    <div className={result.ok ? "field-hint" : "field-error"} role={result.ok ? "status" : "alert"}>
      {result.messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  );
}

async function send(url: string, method: string, body: unknown) {
  return fetch(url, { method, headers: { "content-type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
}

/**
 * Work on an open alert (spec 027 US6 scenario 4): dismiss it, or open an incident from it, which takes the alert's
 * severity and runs. The API checks that the person may change records of the agent's unit.
 */
export function AlertActions({ alert }: { alert: AgentAlert }) {
  const t = useTranslations("agents.security");
  const router = useRouter();
  const ids = useId();
  const [title, setTitle] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);

  async function dismiss() {
    setBusy(true);
    setResult(null);
    try {
      const response = await send(`/api/v1/agents/security/alerts/${encodeURIComponent(alert.id)}/dismiss`, "POST", undefined);
      if (response.ok) {
        setResult({ ok: true, messages: [t("alert.dismissed")] });
        router.refresh();
      } else {
        setResult({ ok: false, messages: response.status === 404 ? [t("notYours")] : await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  async function open(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setResult(null);
    try {
      const response = await send("/api/v1/agents/security/incidents", "POST", { title, alertId: alert.id });
      if (response.ok) {
        const incident = (await response.json()) as AgentIncident;
        router.push(`/admin/agents/security/incidents/${incident.id}`);
      } else {
        setResult({ ok: false, messages: response.status === 404 ? [t("notYours")] : await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  if (alert.status !== "open") {
    return alert.incidentId ? (
      <ButtonLink href={`/admin/agents/security/incidents/${alert.incidentId}`} icon="arrow-forward">
        {t("alert.openIncident")}
      </ButtonLink>
    ) : null;
  }

  return (
    <section className="agents-section" aria-labelledby={`${ids}-work`}>
      <h2 id={`${ids}-work`}>{t("alert.work")}</h2>
      <form className="agents-row" onSubmit={(e) => void open(e)}>
        <div className="field">
          <label htmlFor={`${ids}-title`}>{t("incident.title")}</label>
          <input id={`${ids}-title`} type="text" required maxLength={200} dir="auto" value={title} onChange={(e) => setTitle(e.target.value)} />
        </div>
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
          {t("alert.toIncident")}
        </Button>
        <Button type="button" variant="quiet" onClick={() => void dismiss()} disabled={busy}>
          {t("alert.dismiss")}
        </Button>
      </form>
      <Messages result={result} />
    </section>
  );
}

/**
 * Work on an incident (spec 027 US6 scenario 5): record an action taken, change the severity, close it with a note,
 * and export it as evidence. Each change carries the version the person read, so two officers never overwrite each other.
 */
export function IncidentWork({ incident }: { incident: AgentIncident }) {
  const t = useTranslations("agents.security");
  const router = useRouter();
  const ids = useId();
  const [action, setAction] = useState("");
  const [severity, setSeverity] = useState(incident.severity);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const base = `/api/v1/agents/security/incidents/${encodeURIComponent(incident.id)}`;

  async function run(call: () => Promise<Response>, done: string, reset: () => void) {
    setBusy(true);
    setResult(null);
    try {
      const response = await call();
      if (response.ok) {
        reset();
        setResult({ ok: true, messages: [done] });
        router.refresh();
      } else {
        setResult({ ok: false, messages: response.status === 409 ? [t("incident.stale")] : await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const evidence = (
    <ButtonLink href={`${base}/evidence`} icon="download" download={`incident-${incident.id}.json`}>
      {t("incident.evidence")}
    </ButtonLink>
  );

  if (incident.status !== "open") {
    return (
      <>
        <div className="agents-row">{evidence}</div>
        <Messages result={result} />
      </>
    );
  }

  return (
    <>
      <form
        className="agents-section"
        aria-labelledby={`${ids}-action`}
        onSubmit={(e) => {
          e.preventDefault();
          void run(
            () => send(base, "PATCH", { version: incident.version, severity, action: action.trim() || null }),
            t("incident.saved"),
            () => setAction(""),
          );
        }}
      >
        <h2 id={`${ids}-action`}>{t("incident.record")}</h2>
        <div className="agents-row">
          <div className="field">
            <label htmlFor={`${ids}-severity`}>{t("severity")}</label>
            <select id={`${ids}-severity`} value={severity} onChange={(e) => setSeverity(e.target.value)}>
              {SEVERITIES.map((s) => (
                <option key={s} value={s}>
                  {t(`severities.${s}`)}
                </option>
              ))}
            </select>
          </div>
        </div>
        <div className="field">
          <label htmlFor={`${ids}-text`}>{t("incident.actionTaken")}</label>
          <textarea id={`${ids}-text`} rows={3} maxLength={2000} dir="auto" value={action} onChange={(e) => setAction(e.target.value)} />
        </div>
        <div className="agents-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("incident.save")}
          </Button>
          {evidence}
        </div>
      </form>
      <form
        className="agents-section"
        aria-labelledby={`${ids}-close`}
        onSubmit={(e) => {
          e.preventDefault();
          void run(() => send(`${base}/close`, "POST", { version: incident.version, note }), t("incident.closed"), () => setNote(""));
        }}
      >
        <h2 id={`${ids}-close`}>{t("incident.closeHeading")}</h2>
        <div className="field">
          <label htmlFor={`${ids}-note`}>{t("incident.closureNote")}</label>
          <textarea id={`${ids}-note`} rows={3} required maxLength={2000} dir="auto" value={note} onChange={(e) => setNote(e.target.value)} />
        </div>
        <Button type="submit" busy={busy} busyLabel={t("working")}>
          {t("incident.close")}
        </Button>
      </form>
      <Messages result={result} />
    </>
  );
}
