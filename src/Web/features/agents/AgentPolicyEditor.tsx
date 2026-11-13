"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import {
  AUTONOMY_BUILT,
  CLASSIFICATIONS,
  EXTERNAL_CLASSIFICATIONS,
  type AgentPolicy,
  type AgentPolicyView,
  type ExternalAiPolicy,
  type ToolView,
  tenantToolTitle,
} from "@/lib/agents/types";

type Result = { ok: boolean; messages: string[] } | null;

const RULES = ["execute", "draft_only", "confirm", "approval", "block"] as const;

/**
 * The tenant agent policy (spec 027 US8): the autonomy cap, the highest classification agents may read, which tools
 * agents may hold and stricter approval rules per tool, and the per-run limits. Saving suspends published agents that
 * break the new policy; the API checks every value and the person's permission.
 */
export function AgentPolicyEditor({ initial, tools }: { initial: AgentPolicyView; tools: ToolView[] }) {
  const t = useTranslations("agents.policy");
  const ts = useTranslations("agents");
  const locale = useLocale();
  const router = useRouter();
  const ids = useId();
  const [view, setView] = useState(initial);
  const [policy, setPolicy] = useState<AgentPolicy>(initial.policy);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const set = (patch: Partial<AgentPolicy>) => setPolicy({ ...policy, ...patch });
  const toolLabel = (name: string) => (ts.has(`toolNames.${name}`) ? ts(`toolNames.${name}`) : (tenantToolTitle(tools, name, locale) ?? name));
  const allowed = policy.allowedTools;
  const external: ExternalAiPolicy = policy.external ?? { enabled: true, clients: [], allowedTools: null, writeTools: false };
  const clients = external.clients ?? [];
  const setExternal = (patch: Partial<ExternalAiPolicy>) => set({ external: { ...external, ...patch } });
  const [newClient, setNewClient] = useState("");

  async function save() {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch("/api/v1/agents/policy", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ version: view.version, policy }),
      });
      if (response.ok) {
        const saved = (await response.json()) as AgentPolicyView;
        setView(saved);
        setPolicy(saved.policy);
        setResult({ ok: true, messages: [t("saved")] });
        router.refresh();
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="agents-section" aria-labelledby={`${ids}-title`}>
      <h2 id={`${ids}-title`}>{t("heading")}</h2>
      <p>{t("intro")}</p>
      <div className="agents-row">
        <div className="field">
          <label htmlFor={`${ids}-autonomy`}>{t("maxAutonomy")}</label>
          <select id={`${ids}-autonomy`} value={policy.maxAutonomy} onChange={(e) => set({ maxAutonomy: e.target.value })}>
            {AUTONOMY_BUILT.map((v) => (
              <option key={v} value={v}>
                {ts(`editor.autonomyLevels.${v}`)}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor={`${ids}-class`}>{t("maxClassification")}</label>
          <select id={`${ids}-class`} value={policy.maxClassification} onChange={(e) => set({ maxClassification: e.target.value })}>
            {CLASSIFICATIONS.map((v) => (
              <option key={v} value={v}>
                {ts(`classifications.${v}`)}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor={`${ids}-calls`}>{t("maxToolCalls")}</label>
          <input id={`${ids}-calls`} type="number" min={1} max={50} value={policy.maxToolCalls} onChange={(e) => set({ maxToolCalls: Number(e.target.value) })} />
        </div>
        <div className="field">
          <label htmlFor={`${ids}-steps`}>{t("maxSteps")}</label>
          <input id={`${ids}-steps`} type="number" min={10} max={100} value={policy.maxSteps} onChange={(e) => set({ maxSteps: Number(e.target.value) })} />
        </div>
        <div className="field">
          <label htmlFor={`${ids}-seconds`}>{t("maxRunSeconds")}</label>
          <input
            id={`${ids}-seconds`}
            type="number"
            min={30}
            max={300}
            value={policy.maxRunSeconds}
            onChange={(e) => set({ maxRunSeconds: Number(e.target.value) })}
          />
        </div>
        <div className="field">
          <label htmlFor={`${ids}-daily`}>{t("maxDailyRunsPerAgent")}</label>
          <input
            id={`${ids}-daily`}
            type="number"
            min={1}
            max={100000}
            value={policy.maxDailyRunsPerAgent}
            onChange={(e) => set({ maxDailyRunsPerAgent: Number(e.target.value) })}
          />
        </div>
      </div>
      <fieldset className="agents-checks">
        <legend>{t("tools")}</legend>
        <label>
          <input type="checkbox" checked={allowed === null} onChange={(e) => set({ allowedTools: e.target.checked ? null : tools.map((x) => x.name) })} />
          {t("allTools")}
        </label>
      </fieldset>
      <div className="agents-table">
        <table data-testid="agent-policy-tools">
          <thead>
            <tr>
              <th scope="col">{t("tool")}</th>
              <th scope="col">{t("allowed")}</th>
              <th scope="col">{t("rule")}</th>
            </tr>
          </thead>
          <tbody>
            {tools.map((tool) => {
              const strict = RULES.indexOf(tool.defaultRule as (typeof RULES)[number]);
              const label = toolLabel(tool.name);
              return (
                <tr key={tool.name}>
                  <th scope="row">{label}</th>
                  <td>
                    <input
                      type="checkbox"
                      aria-label={t("allowTool", { tool: label })}
                      disabled={allowed === null}
                      checked={allowed === null || allowed.includes(tool.name)}
                      onChange={(e) => set({ allowedTools: e.target.checked ? [...(allowed ?? []), tool.name] : (allowed ?? []).filter((x) => x !== tool.name) })}
                    />
                  </td>
                  <td>
                    <select
                      aria-label={t("ruleFor", { tool: label })}
                      value={policy.toolRules?.[tool.name] ?? tool.defaultRule}
                      onChange={(e) => set({ toolRules: { ...(policy.toolRules ?? {}), [tool.name]: e.target.value } })}
                    >
                      {RULES.filter((_, i) => i >= strict).map((r) => (
                        <option key={r} value={r}>
                          {t(`rules.${r}`)}
                        </option>
                      ))}
                    </select>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <fieldset className="agents-checks" data-testid="external-ai-policy">
        <legend>{t("external.heading")}</legend>
        <p className="field-hint">{t("external.intro")}</p>
        <label>
          <input type="checkbox" checked={external.enabled} onChange={(e) => setExternal({ enabled: e.target.checked })} />
          {t("external.enabled")}
        </label>
        <label>
          <input type="checkbox" checked={external.writeTools} onChange={(e) => setExternal({ writeTools: e.target.checked })} />
          {t("external.writeTools")}
        </label>
      </fieldset>
      <div className="agents-table">
        <table data-testid="external-ai-clients">
          <caption>{t("external.clients")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("external.clientId")}</th>
              <th scope="col">{t("external.cap")}</th>
              <th scope="col">{t("external.actions")}</th>
            </tr>
          </thead>
          <tbody>
            {clients.length === 0 ? (
              <tr>
                <td colSpan={3}>{t("external.none")}</td>
              </tr>
            ) : (
              clients.map((c) => (
                <tr key={c.clientId}>
                  <th scope="row">
                    <bdi>{c.clientId}</bdi>
                  </th>
                  <td>
                    <select
                      aria-label={t("external.capFor", { client: c.clientId })}
                      value={c.maxClassification}
                      onChange={(e) =>
                        setExternal({ clients: clients.map((x) => (x.clientId === c.clientId ? { ...x, maxClassification: e.target.value } : x)) })
                      }
                    >
                      {EXTERNAL_CLASSIFICATIONS.map((v) => (
                        <option key={v} value={v}>
                          {ts(`classifications.${v}`)}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td>
                    <Button onClick={() => setExternal({ clients: clients.filter((x) => x.clientId !== c.clientId) })}>
                      {t("external.remove", { client: c.clientId })}
                    </Button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
      <div className="agents-row">
        <div className="field">
          <label htmlFor={`${ids}-client`}>{t("external.newClient")}</label>
          <input id={`${ids}-client`} value={newClient} maxLength={200} onChange={(e) => setNewClient(e.target.value)} />
          <p className="field-hint">{t("external.newClientHint")}</p>
        </div>
        <Button
          disabled={newClient.trim() === "" || clients.some((c) => c.clientId === newClient.trim())}
          onClick={() => {
            setExternal({ clients: [...clients, { clientId: newClient.trim(), maxClassification: "internal" }] });
            setNewClient("");
          }}
        >
          {t("external.add")}
        </Button>
      </div>
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="agents-row">
        <Button variant="primary" busy={busy} busyLabel={t("saving")} onClick={() => void save()}>
          {t("save")}
        </Button>
      </div>
    </section>
  );
}
