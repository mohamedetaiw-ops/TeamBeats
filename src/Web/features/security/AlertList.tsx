"use client";

import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { SecurityAlert } from "@/lib/security/types";

type Result = { ok: boolean; messages: string[] } | null;
type Filter = "open" | "handled" | "dismissed" | "all";

const KINDS = [
  "download_volume",
  "privilege_grant_out_of_hours",
  "network_blocked",
  "api_token_reuse",
  "siem_failing",
  "guest_confidential_access",
  "api_denied_burst",
  "api_volume_spike",
];

/** The facts of an alert, as counts and references only (spec 026 FR-013). */
function Facts({ alert }: { alert: SecurityAlert }) {
  const t = useTranslations("security.alerts");
  const format = useFormatter();
  const f = alert.facts;
  const num = (v: unknown) => (typeof v === "number" ? v : 0);
  switch (alert.kind) {
    case "download_volume":
      return (
        <p>
          {t("facts.downloads", {
            files: num(f.files),
            size: format.number(num(f.bytes) / 1_000_000, { maximumFractionDigits: 1 }),
            libraries: Array.isArray(f.libraries) ? f.libraries.length : 0,
          })}
        </p>
      );
    case "privilege_grant_out_of_hours":
      return <p>{t("facts.grant", { role: typeof f.role === "string" ? f.role : "" })}</p>;
    case "network_blocked":
      return (
        <p>
          {t(f.area === "admin" ? "facts.blockedAdmin" : "facts.blockedTenant")} <bdi dir="ltr">{typeof f.address === "string" ? f.address : ""}</bdi>
        </p>
      );
    case "guest_confidential_access":
      return <p>{t("facts.guestConfidential")}</p>;
    case "api_denied_burst":
      return <p>{t("facts.apiDenied", { app: typeof f.appName === "string" ? f.appName : "", count: num(f.count) })}</p>;
    case "api_volume_spike":
      return (
        <p>
          {t("facts.apiVolume", {
            app: typeof f.appName === "string" ? f.appName : "",
            calls: num(f.calls),
            average: format.number(num(f.hourlyAverage), { maximumFractionDigits: 1 }),
          })}
        </p>
      );
    case "siem_failing":
      return <p>{t("facts.siemFailing", { name: typeof f.name === "string" ? f.name : "" })}</p>;
    default:
      return null;
  }
}

function HandleForm({ alert, onHandled }: { alert: SecurityAlert; onHandled: (a: SecurityAlert) => void }) {
  const t = useTranslations("security.alerts");
  const noteId = useId();
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);

  async function handle(outcome: "handled" | "dismissed") {
    setBusy(outcome);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/security/alerts/${alert.id}/handle`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ outcome, note: note.trim() || null }),
      });
      if (response.ok) {
        onHandled({ ...alert, ...((await response.json()) as SecurityAlert), subjectName: alert.subjectName });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="security-item-actions">
      <div className="field">
        <label htmlFor={noteId}>{t("note")}</label>
        <textarea id={noteId} maxLength={1000} value={note} onChange={(e) => setNote(e.target.value)} />
      </div>
      <div className="security-row">
        <Button variant="primary" busy={busy === "handled"} busyLabel={t("working")} onClick={() => void handle("handled")}>
          {t("markHandled")}
        </Button>
        <Button busy={busy === "dismissed"} busyLabel={t("working")} onClick={() => void handle("dismissed")}>
          {t("dismiss")}
        </Button>
      </div>
      {result ? (
        <div role="alert" className="notice notice-error">
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/**
 * Security alerts (spec 026 US5): open alerts first, each with its facts and a handled or dismissed outcome with a
 * note. The API checks who may see and handle alerts; a refused action shows the API's message.
 */
export function AlertList({ initial }: { initial: SecurityAlert[] }) {
  const t = useTranslations("security.alerts");
  const format = useFormatter();
  const [alerts, setAlerts] = useState(initial);
  const [filter, setFilter] = useState<Filter>("open");
  const [closed, setClosed] = useState(false);
  const filterId = useId();

  async function load(next: Filter) {
    setFilter(next);
    setClosed(false);
    const response = await fetch(`/api/v1/security/alerts${next === "all" ? "" : `?status=${next}`}`);
    if (response.ok) setAlerts((await response.json()) as SecurityAlert[]);
  }

  const shown = filter === "all" ? alerts : alerts.filter((a) => a.status === filter);

  return (
    <div className="security">
      <div className="field">
        <label htmlFor={filterId}>{t("filter")}</label>
        <select id={filterId} value={filter} onChange={(e) => void load(e.target.value as Filter)}>
          {(["open", "handled", "dismissed", "all"] as const).map((f) => (
            <option key={f} value={f}>
              {t(`filters.${f}`)}
            </option>
          ))}
        </select>
      </div>
      {closed ? (
        <p role="status" className="notice">
          {t("done")}
        </p>
      ) : null}
      {shown.length === 0 ? (
        <p className="notice">{t("empty")}</p>
      ) : (
        <ul className="security-list">
          {shown.map((alert) => (
            <li key={alert.id} className="security-item">
              <h2>{KINDS.includes(alert.kind) ? t(`kinds.${alert.kind}`) : alert.kind}</h2>
              <p>
                {t("raised", { at: format.dateTime(new Date(alert.createdAt), { dateStyle: "medium", timeStyle: "short" }) })}
                {alert.subjectName ? (
                  <>
                    {" "}
                    {t("subject")} <bdi>{alert.subjectName}</bdi>
                  </>
                ) : null}
              </p>
              <Facts alert={alert} />
              {alert.status === "open" ? (
                <HandleForm alert={alert} onHandled={(a) => {
                    setAlerts((list) => list.map((x) => (x.id === a.id ? a : x)));
                    setClosed(true);
                  }} />
              ) : (
                <p>
                  {t(`outcome.${alert.status}`)}
                  {alert.handledByName ? (
                    <>
                      {" "}
                      {t("by")} <bdi>{alert.handledByName}</bdi>
                    </>
                  ) : null}
                  {alert.note ? (
                    <>
                      {" "}
                      <bdi>{alert.note}</bdi>
                    </>
                  ) : null}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
