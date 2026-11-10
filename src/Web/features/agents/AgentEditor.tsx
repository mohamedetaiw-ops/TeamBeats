"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import type { OrgUnit } from "@/lib/experience/builder-types";
import {
  AUTONOMY_BUILT,
  OUTPUT_FORMATS,
  pickText,
  TONES,
  type AgentDefinition,
  type AgentDetail,
  type Localized,
  type RunView,
  type ToolView,
} from "@/lib/agents/types";

type Result = { ok: boolean; messages: string[] } | null;

const LANGUAGES = ["en", "ar"] as const;

/** A text field per language, Arabic laid out right to left. */
function Bilingual({
  label,
  value,
  onChange,
  multiline,
  disabled,
}: {
  label: string;
  value: Localized | null;
  onChange: (value: Localized) => void;
  multiline?: boolean;
  disabled: boolean;
}) {
  const t = useTranslations("agents.editor");
  const id = useId();
  return (
    <div className="agents-pair">
      {LANGUAGES.map((lang) => {
        const fieldId = `${id}-${lang}`;
        const common = {
          id: fieldId,
          dir: lang === "ar" ? "rtl" : "ltr",
          lang,
          value: value?.[lang] ?? "",
          disabled,
          onChange: (e: { target: { value: string } }) => onChange({ ...(value ?? {}), [lang]: e.target.value }),
        } as const;
        return (
          <div className="field" key={lang}>
            <label htmlFor={fieldId}>{t("inLanguage", { field: label, language: t(`languages.${lang}`) })}</label>
            {multiline ? <textarea rows={6} {...common} /> : <input type="text" {...common} />}
          </div>
        );
      })}
    </div>
  );
}

/**
 * The agent editor (spec 020 US1, T014): the definition fields of section 12 in Arabic and English, the tools of the
 * registry the policy allows, autonomy up to L2, the audience, the test panel, and the lifecycle: submit for review,
 * suspend, resume, retire and rollback. The API checks every change against the schema, the tool registry, the
 * policy and the person's permissions; the page shows what it answers.
 */
export function AgentEditor({ initial, tools, units }: { initial: AgentDetail; tools: ToolView[]; units: OrgUnit[] }) {
  const t = useTranslations("agents.editor");
  const ts = useTranslations("agents");
  const locale = useLocale();
  const router = useRouter();
  const ids = useId();
  const [detail, setDetail] = useState(initial);
  const [definition, setDefinition] = useState<AgentDefinition>(initial.definition);
  const [busy, setBusy] = useState<string | null>(null);
  const [owner, setOwner] = useState<string | null>(initial.agent.ownerUserId);
  const [backup, setBackup] = useState<string | null>(initial.agent.backupOwnerUserId);
  const [result, setResult] = useState<Result>(null);
  const [testMessage, setTestMessage] = useState("");
  const [testRun, setTestRun] = useState<RunView | null>(null);
  const [reason, setReason] = useState("");
  const [rollbackTo, setRollbackTo] = useState("");

  const status = detail.agent.status;
  const locked = detail.latest.state === "in_review";
  const editable = detail.canEdit && !locked && status !== "retired";
  const set = (patch: Partial<AgentDefinition>) => setDefinition({ ...definition, ...patch });
  const ownersEditable = detail.canEdit && status !== "retired";
  const audienceUnits = definition.audience?.orgUnits ?? [];
  const earlier = detail.versions.filter((v) => v.publishedAt !== null && v.number !== detail.agent.currentVersion);
  const toolLabel = (name: string) => (ts.has(`toolNames.${name}`) ? ts(`toolNames.${name}`) : name);

  async function send(key: string, url: string, method: string, body: unknown, done: string) {
    setBusy(key);
    setResult(null);
    try {
      const response = await fetch(url, { method, headers: { "content-type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
      if (response.ok) {
        const next = (await response.json()) as AgentDetail;
        setDetail(next);
        setDefinition(next.definition);
        setReason("");
        setResult({ ok: true, messages: [done] });
        router.refresh();
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  async function test() {
    setBusy("test");
    setTestRun(null);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/agents/${detail.agent.id}/test`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ message: testMessage }),
      });
      if (response.ok) {
        setTestRun((await response.json()) as RunView);
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  const base = `/api/v1/agents/${detail.agent.id}`;
  return (
    <div className="agents">
      <p>
        <span className="agents-badge" data-testid="agent-status">
          {ts(`status.${status}`)}
        </span>{" "}
        <span className="agents-badge">{ts(`risk.${detail.agent.riskClass}`)}</span>{" "}
        <span className="agents-badge">{t("versionOf", { latest: String(detail.latest.number), state: ts(`versionState.${detail.latest.state}`) })}</span>
      </p>
      {locked ? (
        <p className="notice">
          {t("inReview")} <Link href={`/admin/agents/${detail.agent.id}/review`}>{t("openReview")}</Link>
        </p>
      ) : null}
      {!detail.canEdit ? <p className="notice">{t("readOnly")}</p> : null}

      <section className="agents-section" aria-labelledby={`${ids}-about`}>
        <h2 id={`${ids}-about`}>{t("about")}</h2>
        <Bilingual label={t("name")} value={definition.name} onChange={(name) => set({ name })} disabled={!editable} />
        <Bilingual label={t("description")} value={definition.description} onChange={(description) => set({ description })} disabled={!editable} />
        <Bilingual label={t("purpose")} value={definition.purpose ?? null} onChange={(purpose) => set({ purpose })} multiline disabled={!editable} />
        <Bilingual label={t("instructions")} value={definition.instructions} onChange={(instructions) => set({ instructions })} multiline disabled={!editable} />
        <div className="agents-row">
          <div className="field">
            <label htmlFor={`${ids}-tone`}>{t("tone")}</label>
            <select id={`${ids}-tone`} value={definition.tone ?? ""} disabled={!editable} onChange={(e) => set({ tone: e.target.value })}>
              {TONES.map((v) => (
                <option key={v} value={v}>
                  {t(`tones.${v}`)}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor={`${ids}-format`}>{t("outputFormat")}</label>
            <select id={`${ids}-format`} value={definition.outputFormat ?? ""} disabled={!editable} onChange={(e) => set({ outputFormat: e.target.value })}>
              {OUTPUT_FORMATS.map((v) => (
                <option key={v} value={v}>
                  {t(`formats.${v}`)}
                </option>
              ))}
            </select>
          </div>
        </div>
      </section>

      <section className="agents-section" aria-labelledby={`${ids}-abilities`}>
        <h2 id={`${ids}-abilities`}>{t("abilities")}</h2>
        <div className="agents-row">
          <div className="field">
            <label htmlFor={`${ids}-autonomy`}>{t("autonomy")}</label>
            <select id={`${ids}-autonomy`} value={definition.autonomy ?? ""} disabled={!editable} onChange={(e) => set({ autonomy: e.target.value })}>
              {AUTONOMY_BUILT.map((v) => (
                <option key={v} value={v}>
                  {t(`autonomyLevels.${v}`)}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor={`${ids}-calls`}>{t("maxToolCalls")}</label>
            <input
              id={`${ids}-calls`}
              type="number"
              min={1}
              max={50}
              disabled={!editable}
              value={definition.maxToolCalls ?? ""}
              onChange={(e) => set({ maxToolCalls: e.target.value === "" ? null : Number(e.target.value) })}
            />
          </div>
        </div>
        <fieldset className="agents-checks">
          <legend>{t("tools")}</legend>
          {tools.map((tool) => (
            <label key={tool.name}>
              <input
                type="checkbox"
                disabled={!editable || (!tool.allowed && !(definition.tools ?? []).includes(tool.name))}
                checked={(definition.tools ?? []).includes(tool.name)}
                onChange={(e) =>
                  set({ tools: e.target.checked ? [...(definition.tools ?? []), tool.name] : (definition.tools ?? []).filter((x) => x !== tool.name) })
                }
              />
              <span>
                {toolLabel(tool.name)} <span className="agents-badge">{t(`sideEffects.${tool.sideEffect}`)}</span>
                {tool.allowed ? null : <span className="agents-badge">{t("toolBlocked")}</span>}
              </span>
            </label>
          ))}
        </fieldset>
        <Bilingual label={t("escalation")} value={definition.escalation} onChange={(escalation) => set({ escalation })} disabled={!editable} />
      </section>

      <section className="agents-section" aria-labelledby={`${ids}-owners`} data-testid="agent-owners">
        <h2 id={`${ids}-owners`}>{t("owners.title")}</h2>
        {detail.agent.ownerFlaggedAt ? <p className="notice">{t("owners.flagged")}</p> : null}
        <PersonPicker label={t("owners.owner")} value={owner} onChange={setOwner} readOnly={!ownersEditable} />
        <PersonPicker label={t("owners.backup")} value={backup} onChange={setBackup} readOnly={!ownersEditable} />
        {ownersEditable ? (
          <div className="agents-row">
            <Button
              busy={busy === "owners"}
              busyLabel={t("saving")}
              disabled={owner === null}
              onClick={() => void send("owners", `${base}/owners`, "PUT", { ownerUserId: owner, backupOwnerUserId: backup }, t("owners.saved"))}
            >
              {t("owners.save")}
            </Button>
          </div>
        ) : null}
      </section>

      <section className="agents-section" aria-labelledby={`${ids}-audience`}>
        <h2 id={`${ids}-audience`}>{t("audience")}</h2>
        {definition.audience?.scope === "tenant" ? (
          <p>{t("audienceTenant")}</p>
        ) : (
          <fieldset className="agents-checks">
            <legend>{t("audienceUnits")}</legend>
            {units.map((u) => (
              <label key={u.id}>
                <input
                  type="checkbox"
                  disabled={!editable}
                  checked={audienceUnits.includes(u.id)}
                  onChange={(e) =>
                    set({
                      audience: {
                        scope: "org_units",
                        orgUnits: e.target.checked ? [...audienceUnits, u.id] : audienceUnits.filter((x) => x !== u.id),
                      },
                    })
                  }
                />
                <bdi>{pickText(u.names, locale)}</bdi>
              </label>
            ))}
          </fieldset>
        )}
      </section>

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      {editable ? (
        <div className="agents-row">
          <Button
            variant="primary"
            busy={busy === "save"}
            busyLabel={t("saving")}
            onClick={() => void send("save", `${base}/draft`, "PUT", { revision: detail.latest.revision, definition }, t("saved"))}
          >
            {t("save")}
          </Button>
          {detail.latest.state === "draft" ? (
            <Button busy={busy === "submit"} busyLabel={t("saving")} onClick={() => void send("submit", `${base}/submit`, "POST", { revision: detail.latest.revision }, t("submitted"))}>
              {t("submit")}
            </Button>
          ) : null}
        </div>
      ) : null}

      {detail.canEdit && status !== "retired" ? (
        <section className="agents-section" aria-labelledby={`${ids}-test`} data-testid="agent-test-panel">
          <h2 id={`${ids}-test`}>{t("test.title")}</h2>
          <p>{t("test.intro")}</p>
          <div className="field">
            <label htmlFor={`${ids}-test-message`}>{t("test.message")}</label>
            <textarea id={`${ids}-test-message`} rows={3} value={testMessage} onChange={(e) => setTestMessage(e.target.value)} />
          </div>
          <div className="agents-row">
            <Button busy={busy === "test"} busyLabel={t("test.running")} disabled={testMessage.trim() === ""} onClick={() => void test()}>
              {t("test.run")}
            </Button>
          </div>
          {testRun ? (
            <div role="status" data-testid="agent-test-result">
              <p>{t("test.outcome", { outcome: ts(`outcomes.${testRun.outcome ?? "none"}`) })}</p>
              {testRun.answer ? <p className="agents-answer">{testRun.answer}</p> : null}
              {testRun.generated ? <p className="field-hint">{ts("generated")}</p> : null}
            </div>
          ) : null}
        </section>
      ) : null}

      {status === "published" || status === "suspended" ? (
        <section className="agents-section" aria-labelledby={`${ids}-life`}>
          <h2 id={`${ids}-life`}>{t("lifecycle.title")}</h2>
          <div className="field">
            <label htmlFor={`${ids}-reason`}>{t("lifecycle.reason")}</label>
            <textarea id={`${ids}-reason`} rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
          </div>
          <div className="agents-row">
            {status === "published" ? (
              <Button busy={busy === "suspend"} busyLabel={t("saving")} onClick={() => void send("suspend", `${base}/suspend`, "POST", { reason }, t("lifecycle.suspended"))}>
                {t("lifecycle.suspend")}
              </Button>
            ) : (
              <Button busy={busy === "resume"} busyLabel={t("saving")} onClick={() => void send("resume", `${base}/resume`, "POST", undefined, t("lifecycle.resumed"))}>
                {t("lifecycle.resume")}
              </Button>
            )}
            <Button
              variant="destructive"
              busy={busy === "retire"}
              busyLabel={t("saving")}
              onClick={() => void send("retire", `${base}/retire`, "POST", { reason }, t("lifecycle.retired"))}
            >
              {t("lifecycle.retire")}
            </Button>
          </div>
          {earlier.length > 0 ? (
            <div className="agents-row">
              <div className="field">
                <label htmlFor={`${ids}-rollback`}>{t("lifecycle.rollbackTo")}</label>
                <select id={`${ids}-rollback`} value={rollbackTo} onChange={(e) => setRollbackTo(e.target.value)}>
                  <option value="">{t("lifecycle.choose")}</option>
                  {earlier.map((v) => (
                    <option key={v.number} value={v.number}>
                      {t("lifecycle.version", { number: String(v.number) })}
                    </option>
                  ))}
                </select>
              </div>
              <Button
                disabled={rollbackTo === ""}
                busy={busy === "rollback"}
                busyLabel={t("saving")}
                onClick={() => void send("rollback", `${base}/rollback`, "POST", { version: Number(rollbackTo) }, t("lifecycle.rolledBack"))}
              >
                {t("lifecycle.rollback")}
              </Button>
            </div>
          ) : null}
        </section>
      ) : null}
    </div>
  );
}
