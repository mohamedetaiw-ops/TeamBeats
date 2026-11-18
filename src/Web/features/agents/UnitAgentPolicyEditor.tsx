"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { AUTONOMY_BUILT, CLASSIFICATIONS, type ToolView, type UnitAgentPolicy, type UnitAgentPolicyView, tenantToolTitle } from "@/lib/agents/types";
import type { OrgUnitView } from "@/lib/admin/types";

type Result = { ok: boolean; messages: string[] } | null;

const LIMITS = ["maxToolCalls", "maxSteps", "maxRunSeconds", "maxDailyRunsPerAgent"] as const;
type Limit = (typeof LIMITS)[number];

const EMPTY: UnitAgentPolicy = {
  maxAutonomy: null,
  maxClassification: null,
  allowedTools: null,
  toolRules: null,
  scheduledAgents: null,
  persistentMemory: null,
  maxToolCalls: null,
  maxSteps: null,
  maxRunSeconds: null,
  maxDailyRunsPerAgent: null,
};

function pick(names: Record<string, string> | undefined, locale: string): string {
  if (!names) return "";
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
}

/**
 * An org unit's agent policy (spec 027 US8 scenarios 3 and 4, T016): an AI policy administrator of the unit, or of the
 * whole organization, narrows autonomy, classification, tools, scheduled agents, memory and the run limits for the
 * unit's agents and the units below it. Every field shows what it inherits; the API refuses anything looser than the
 * policy above and names that rule, and saving suspends the unit's published agents that break the result.
 */
export function UnitAgentPolicyEditor({ units, tools }: { units: OrgUnitView[]; tools: ToolView[] }) {
  const t = useTranslations("agents.unitPolicy");
  const tp = useTranslations("agents.policy");
  const ts = useTranslations("agents");
  const locale = useLocale();
  const ids = useId();
  const [unit, setUnit] = useState(units[0]?.id ?? "");
  const [view, setView] = useState<UnitAgentPolicyView | null>(null);
  const [policy, setPolicy] = useState<UnitAgentPolicy>(EMPTY);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const set = (patch: Partial<UnitAgentPolicy>) => setPolicy({ ...policy, ...patch });
  const toolLabel = (name: string) => (ts.has(`toolNames.${name}`) ? ts(`toolNames.${name}`) : (tenantToolTitle(tools, name, locale) ?? name));
  const unitName = (id: string) => pick(units.find((u) => u.id === id)?.names, locale) || t("unknownUnit");

  async function load(id: string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/agents/policy/units/${encodeURIComponent(id)}`, { cache: "no-store" });
      if (response.ok) {
        const next = (await response.json()) as UnitAgentPolicyView;
        setView(next);
        setPolicy({ ...EMPTY, ...next.policy });
      } else {
        setView(null);
        setResult({ ok: false, messages: response.status === 404 ? [t("notYours")] : await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [tp("failed")] });
    } finally {
      setBusy(false);
    }
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!view) return;
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/agents/policy/units/${encodeURIComponent(view.unitId)}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ version: view.version, policy }),
      });
      if (response.ok) {
        const next = (await response.json()) as UnitAgentPolicyView;
        setView(next);
        setPolicy({ ...EMPTY, ...next.policy });
        setResult({ ok: true, messages: [t("saved", { unit: unitName(next.unitId) })] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [tp("failed")] });
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!view) return;
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/agents/policy/units/${encodeURIComponent(view.unitId)}`, { method: "DELETE" });
      if (response.ok) {
        const id = view.unitId;
        await load(id);
        setResult({ ok: true, messages: [t("removed", { unit: unitName(id) })] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [tp("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const inherited = view?.inherited;
  const inheritedTools = inherited?.allowedTools ?? tools.map((tool) => tool.name);
  const limitValue = (key: Limit) => (policy[key] == null ? "" : String(policy[key]));

  return (
    <div className="agents">
      <section className="agents-section" aria-labelledby={`${ids}-pick`}>
        <h2 id={`${ids}-pick`}>{t("pickHeading")}</h2>
        <p>{t("intro")}</p>
        <div className="agents-row">
          <div className="field">
            <label htmlFor={`${ids}-unit`}>{t("unit")}</label>
            <select id={`${ids}-unit`} value={unit} onChange={(e) => setUnit(e.target.value)}>
              {units.map((u) => (
                <option key={u.id} value={u.id}>
                  {pick(u.names, locale)}
                </option>
              ))}
            </select>
          </div>
          <Button type="button" onClick={() => void load(unit)} disabled={!unit} busy={busy && !view} busyLabel={tp("saving")}>
            {t("open")}
          </Button>
        </div>
        {result ? (
          <div className={result.ok ? "field-hint" : "field-error"} role={result.ok ? "status" : "alert"}>
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </section>

      {view && inherited ? (
        <form className="agents-section" aria-labelledby={`${ids}-edit`} onSubmit={(e) => void save(e)}>
          <h2 id={`${ids}-edit`}>{t("heading", { unit: unitName(view.unitId) })}</h2>
          <p className="field-hint">{t("inheritHint")}</p>
          <div className="agents-row">
            <div className="field">
              <label htmlFor={`${ids}-autonomy`}>{tp("maxAutonomy")}</label>
              <select id={`${ids}-autonomy`} value={policy.maxAutonomy ?? ""} onChange={(e) => set({ maxAutonomy: e.target.value || null })}>
                <option value="">{t("inherit", { value: inherited.maxAutonomy })}</option>
                {AUTONOMY_BUILT.map((a) => (
                  <option key={a} value={a}>
                    {a}
                  </option>
                ))}
              </select>
            </div>
            <div className="field">
              <label htmlFor={`${ids}-class`}>{tp("maxClassification")}</label>
              <select id={`${ids}-class`} value={policy.maxClassification ?? ""} onChange={(e) => set({ maxClassification: e.target.value || null })}>
                <option value="">{t("inherit", { value: ts(`classifications.${inherited.maxClassification}`) })}</option>
                {CLASSIFICATIONS.map((c) => (
                  <option key={c} value={c}>
                    {ts(`classifications.${c}`)}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="agents-row">
            {LIMITS.map((key) => (
              <div className="field" key={key}>
                <label htmlFor={`${ids}-${key}`}>{tp(key)}</label>
                <input
                  id={`${ids}-${key}`}
                  type="number"
                  inputMode="numeric"
                  min={1}
                  max={inherited[key]}
                  value={limitValue(key)}
                  placeholder={String(inherited[key])}
                  aria-describedby={`${ids}-${key}-hint`}
                  onChange={(e) => set({ [key]: e.target.value === "" ? null : Number(e.target.value) } as Partial<UnitAgentPolicy>)}
                />
                <p id={`${ids}-${key}-hint`} className="field-hint">
                  {t("inherit", { value: inherited[key] })}
                </p>
              </div>
            ))}
          </div>
          <fieldset className="agents-checks">
            <legend>{t("switches")}</legend>
            <label>
              <input
                type="checkbox"
                checked={policy.scheduledAgents === false}
                disabled={!inherited.scheduledAgents}
                onChange={(e) => set({ scheduledAgents: e.target.checked ? false : null })}
              />
              {t("noScheduled")}
            </label>
            <label>
              <input
                type="checkbox"
                checked={policy.persistentMemory === false}
                disabled={!inherited.persistentMemory}
                onChange={(e) => set({ persistentMemory: e.target.checked ? false : null })}
              />
              {t("noMemory")}
            </label>
          </fieldset>
          <fieldset className="agents-checks">
            <legend>{tp("tools")}</legend>
            <label>
              <input type="checkbox" checked={policy.allowedTools === null} onChange={(e) => set({ allowedTools: e.target.checked ? null : [...inheritedTools] })} />
              {t("allInherited")}
            </label>
            {policy.allowedTools !== null
              ? inheritedTools.map((name) => (
                  <label key={name}>
                    <input
                      type="checkbox"
                      checked={policy.allowedTools?.includes(name) ?? false}
                      onChange={(e) =>
                        set({
                          allowedTools: e.target.checked ? [...(policy.allowedTools ?? []).filter((x) => x !== name), name] : (policy.allowedTools ?? []).filter((x) => x !== name),
                        })
                      }
                    />
                    <bdi>{toolLabel(name)}</bdi>
                  </label>
                ))
              : null}
          </fieldset>
          <div className="agents-row">
            <Button type="submit" variant="primary" busy={busy} busyLabel={tp("saving")}>
              {t("save")}
            </Button>
            {view.updatedAt ? (
              <Button type="button" variant="quiet" icon="trash" onClick={() => void remove()} disabled={busy}>
                {t("remove")}
              </Button>
            ) : null}
          </div>
        </form>
      ) : null}
    </div>
  );
}
