"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { AGENT_JOB_STATUSES, known, type AgentJob, type EnrolmentCode, type SyncAgent } from "@/lib/integrations/types";
import { ErrorText, Messages } from "./parts";
import { send } from "./request";

/**
 * The sync agents of an on-premises Active Directory connection (spec 017 US6): a one-time enrolment code (shown once),
 * the enrolled agents with whether each is in touch, revoking one, the directory reads with their results, starting a
 * read now, and using the connection for directory sync (spec 003 US6). Every rule is checked by the API.
 */
export function SyncAgentPanel({ connectionId, agents: initialAgents, jobs: initialJobs }: { connectionId: string; agents: SyncAgent[]; jobs: AgentJob[] }) {
  const t = useTranslations("connections");
  const format = useFormatter();
  const returnTo = `/admin/connections/${connectionId}`;
  const base = `/api/v1/integrations/pack-connections/${connectionId}`;
  const [agents, setAgents] = useState(initialAgents);
  const [jobs, setJobs] = useState(initialJobs);
  const [code, setCode] = useState<EnrolmentCode | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : t("detail.never"));

  async function act<T>(key: string, method: string, path: string, body: unknown, done: (data: T) => Promise<string | null> | string | null) {
    setBusy(key);
    setMessages({ ok: true, list: [] });
    const result = await send<T>(method, path, body, returnTo, t("detail.failed"));
    if (result.ok) {
      const note = await done(result.data);
      setMessages({ ok: true, list: note ? [note] : [] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  async function reload() {
    const [a, j] = await Promise.all([fetch(`${base}/agents`), fetch(`${base}/agent-jobs`)]);
    if (a.ok) setAgents((await a.json()) as SyncAgent[]);
    if (j.ok) setJobs((await j.json()) as AgentJob[]);
  }

  return (
    <section className="connections-section" aria-labelledby="agents-heading">
      <h2 id="agents-heading">{t("agents.title")}</h2>
      <p>{t("agents.intro")}</p>

      <h3>{t("agents.enrolTitle")}</h3>
      <p>{t("agents.enrolIntro")}</p>
      {code ? (
        <dl className="connections-facts" aria-live="polite">
          <dt>{t("agents.code")}</dt>
          <dd>
            <bdi dir="ltr">
              <code>{code.code}</code>
            </bdi>
          </dd>
          <dt>{t("agents.codeExpires")}</dt>
          <dd>{date(code.expiresAt)}</dd>
        </dl>
      ) : null}
      <div className="connections-row">
        <Button busy={busy === "code"} busyLabel={t("detail.working")} onClick={() => void act<EnrolmentCode>("code", "POST", `${base}/agents/enrolment-code`, undefined, (c) => (setCode(c), t("agents.codeMade")))}>
          {t("agents.makeCode")}
        </Button>
      </div>

      <h3>{t("agents.listTitle")}</h3>
      {agents.length === 0 ? (
        <p className="notice">{t("agents.empty")}</p>
      ) : (
        <ul className="connections-list">
          {agents.map((a) => (
            <li key={a.id} className="connections-item">
              <h4>
                <bdi>{a.name}</bdi>
              </h4>
              <p className="connections-row">
                <span className="connections-status" data-status={a.revokedAt ? "disabled" : a.online ? "healthy" : "degraded"}>
                  {t(a.revokedAt ? "agents.revoked" : a.online ? "agents.online" : "agents.offline")}
                </span>
                <span>{t("agents.lastContact", { at: date(a.lastContactAt) })}</span>
                {a.version ? (
                  <span>
                    {t("agents.version")} <bdi dir="ltr">{a.version}</bdi>
                  </span>
                ) : null}
                <span>
                  {t("agents.key")} <bdi dir="ltr">{a.thumbprint.slice(0, 16)}</bdi>
                </span>
              </p>
              {a.revokedAt ? null : (
                <ConfirmAction
                  label={t("agents.revoke")}
                  question={t("agents.revokeQuestion", { name: a.name })}
                  confirmLabel={t("agents.revokeConfirm")}
                  cancelLabel={t("detail.cancel")}
                  onConfirm={() => act<null>(`revoke-${a.id}`, "POST", `${base}/agents/${a.id}:revoke`, undefined, async () => (await reload(), t("agents.revokedNote")))}
                />
              )}
            </li>
          ))}
        </ul>
      )}

      <h3>{t("agents.readsTitle")}</h3>
      <p>{t("agents.readsIntro")}</p>
      <div className="connections-row">
        <Button busy={busy === "read"} busyLabel={t("detail.working")} onClick={() => void act<AgentJob>("read", "POST", `${base}/agent-jobs`, undefined, async () => (await reload(), t("agents.readQueued")))}>
          {t("agents.readNow")}
        </Button>
        <Button
          busy={busy === "directory"}
          busyLabel={t("detail.working")}
          onClick={() => void act<unknown>("directory", "PUT", "/api/v1/tenant/directory", { connectionId }, () => t("agents.directoryUsed"))}
        >
          {t("agents.useForDirectory")}
        </Button>
      </div>
      {jobs.length === 0 ? (
        <p className="notice">{t("agents.noReads")}</p>
      ) : (
        <ul className="connections-list">
          {jobs.map((j) => (
            <li key={j.id} className="connections-row">
              <span>{date(j.createdAt)}</span>
              <span>{known(AGENT_JOB_STATUSES, j.status) ? t(`agents.jobStatus.${j.status}`) : j.status}</span>
              {j.users !== null ? <span>{t("agents.counts", { users: j.users, groups: j.groups ?? 0 })}</span> : null}
              {j.errorClass ? (
                <span>
                  <ErrorText errorClass={j.errorClass} />
                </span>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      <Messages messages={messages.list} ok={messages.ok} />
    </section>
  );
}
