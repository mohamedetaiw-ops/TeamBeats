"use client";

import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";
import { pickText } from "@/lib/agents/types";
import { LOCALES, type AgentOutcome, type OutcomesView } from "@/lib/evaluation/types";

type Draft = { minutes: string; names: Record<string, string> };

const toDraft = (a: AgentOutcome): Draft => ({
  minutes: a.baseline ? String(a.baseline.minutes) : "",
  names: Object.fromEntries(LOCALES.map((l) => [l, a.baseline?.taskNames[l] ?? ""])),
});

/**
 * Business outcomes per agent (spec 034 US9 scenario 1): completion, escalation and resolved without help, and hours
 * saved against the baseline the tenant sets for the task an agent replaces. A group smaller than the minimum is
 * withheld by the API. Only the AI Policy Administrator sets baselines, and the API checks that too.
 */
export function OutcomeBaselines({ initial, locale }: { initial: OutcomesView; locale: string }) {
  const t = useTranslations("evaluation.outcomes");
  const te = useTranslations("evaluation");
  const format = useFormatter();
  const ids = useId();
  const [view, setView] = useState(initial);
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft>({ minutes: "", names: {} });
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const percent = (v: number | null) => (v === null ? te("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));

  async function save(agentId: string, remove: boolean) {
    setBusy(remove ? `${agentId}:remove` : agentId);
    setResult(null);
    const body = remove
      ? { taskNames: null, minutes: null }
      : { taskNames: Object.fromEntries(Object.entries(draft.names).filter(([, v]) => v.trim() !== "")), minutes: draft.minutes.trim() === "" ? null : Number(draft.minutes) };
    const sent = await send<OutcomesView>(`/api/v1/evaluation/outcomes/baselines/${agentId}`, "PUT", body, te("failed"));
    setBusy(null);
    if (sent.ok) {
      setView(sent.data);
      setEditing(null);
      setResult({ ok: true, messages: [remove ? t("removed") : t("saved")] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  return (
    <div className="agents">
      <p>{t("withheldHint", { size: String(view.minGroupSize), days: String(view.days) })}</p>
      {view.agents.length === 0 ? <p>{t("empty")}</p> : null}
      {view.agents.length > 0 ? (
        <div className="agents-table">
          <table data-testid="eval-outcomes">
            <thead>
              <tr>
                <th scope="col">{t("agent")}</th>
                <th scope="col">{t("baseline")}</th>
                <th scope="col">{t("runs")}</th>
                <th scope="col">{t("completion")}</th>
                <th scope="col">{t("escalation")}</th>
                <th scope="col">{t("resolved")}</th>
                <th scope="col">{t("hoursSaved")}</th>
                {view.canEdit ? <th scope="col">{te("suite.actions")}</th> : null}
              </tr>
            </thead>
            <tbody>
              {view.agents.map((a) => {
                const name = pickText(a.agentName, locale) || te("untitled");
                return (
                  <tr key={a.agentId}>
                    <th scope="row">
                      <bdi>{name}</bdi>
                    </th>
                    <td>
                      {a.baseline ? (
                        <>
                          <bdi>{pickText(a.baseline.taskNames, locale)}</bdi> {t("minutesValue", { minutes: format.number(a.baseline.minutes) })}
                        </>
                      ) : (
                        t("noBaseline")
                      )}
                    </td>
                    {a.withheld ? (
                      <td colSpan={5}>{t("withheld")}</td>
                    ) : (
                      <>
                        <td data-number>{a.runs === null ? te("none") : format.number(a.runs)}</td>
                        <td data-number>{percent(a.completionRate)}</td>
                        <td data-number>{percent(a.escalationRate)}</td>
                        <td data-number>{percent(a.resolvedWithoutHelpRate)}</td>
                        <td data-number>{a.hoursSaved === null ? te("none") : format.number(a.hoursSaved, { maximumFractionDigits: 1 })}</td>
                      </>
                    )}
                    {view.canEdit ? (
                      <td>
                        <Button
                          variant="quiet"
                          onClick={() => {
                            setEditing(a.agentId);
                            setDraft(toDraft(a));
                          }}
                        >
                          {t("edit", { agent: name })}
                        </Button>
                      </td>
                    ) : null}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : null}
      {editing && view.canEdit
        ? (() => {
            const agent = view.agents.find((a) => a.agentId === editing);
            if (!agent) return null;
            return (
              <section className="agents-section" aria-labelledby={`${ids}-edit`}>
                <h2 id={`${ids}-edit`}>{t("editTitle", { agent: pickText(agent.agentName, locale) || te("untitled") })}</h2>
                <p>{t("editHint")}</p>
                <div className="agents-row">
                  {LOCALES.map((l) => (
                    <div className="field" key={l}>
                      <label htmlFor={`${ids}-task-${l}`}>{t("taskName", { language: te(`locales.${l}`) })}</label>
                      <input
                        id={`${ids}-task-${l}`}
                        type="text"
                        dir={l === "ar" ? "rtl" : "ltr"}
                        lang={l}
                        maxLength={120}
                        value={draft.names[l] ?? ""}
                        onChange={(e) => setDraft((d) => ({ ...d, names: { ...d.names, [l]: e.target.value } }))}
                      />
                    </div>
                  ))}
                  <div className="field">
                    <label htmlFor={`${ids}-minutes`}>{t("minutes")}</label>
                    <input id={`${ids}-minutes`} type="number" min={0.1} max={480} step="0.1" value={draft.minutes} onChange={(e) => setDraft((d) => ({ ...d, minutes: e.target.value }))} />
                  </div>
                </div>
                <div className="agents-row">
                  <Button variant="primary" busy={busy === editing} busyLabel={te("working")} onClick={() => void save(editing, false)} data-testid="eval-outcome-save">
                    {t("save")}
                  </Button>
                  {agent.baseline ? (
                    <Button busy={busy === `${editing}:remove`} busyLabel={te("working")} onClick={() => void save(editing, true)}>
                      {t("remove")}
                    </Button>
                  ) : null}
                  <Button variant="quiet" onClick={() => setEditing(null)}>
                    {t("cancel")}
                  </Button>
                </div>
              </section>
            );
          })()
        : null}
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}
