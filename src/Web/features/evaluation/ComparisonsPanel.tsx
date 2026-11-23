"use client";

import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";
import type { Comparison, ComparisonsView } from "@/lib/evaluation/types";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * An agent's online comparisons (spec 034 US9 scenarios 2 and 3, FR-026): a challenger version or model proposed with
 * the share of people it gets, approved by the AI Policy Administrator once its gate passed, its production scores per
 * arm against the champion, and stopping it. What each viewer may do comes from the API.
 */
export function ComparisonsPanel({ initial, versions, champion }: { initial: ComparisonsView; versions: number[]; champion: number | null }) {
  const t = useTranslations("evaluation.comparisons");
  const te = useTranslations("evaluation");
  const format = useFormatter();
  const ids = useId();
  const [view, setView] = useState(initial);
  const [kind, setKind] = useState<"version" | "model">("version");
  const challengers = versions.filter((v) => v !== champion);
  const [version, setVersion] = useState(String(challengers[0] ?? ""));
  const [model, setModel] = useState("");
  const [share, setShare] = useState("10");
  const [purpose, setPurpose] = useState("");
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);
  const percent = (v: number | null) => (v === null ? te("none") : format.number(v, { style: "percent", maximumFractionDigits: 1 }));
  const label = (key: string) => (te.has(`metrics.${key}`) ? te(`metrics.${key}`) : key);

  function replace(next: Comparison) {
    setView((current) => ({ ...current, comparisons: [next, ...current.comparisons.filter((c) => c.id !== next.id)] }));
  }

  async function act(key: string, path: string, body: unknown, done: string) {
    setBusy(key);
    setResult(null);
    const sent = await send<Comparison>(path, "POST", body, te("failed"));
    setBusy(null);
    if (sent.ok) {
      replace(sent.data);
      setResult({ ok: true, messages: [done] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  const propose = () =>
    act(
      "propose",
      "/api/v1/evaluation/comparisons",
      {
        agentId: view.agentId,
        challengerVersion: kind === "version" ? Number(version) : null,
        challengerModel: kind === "model" ? model.trim() : null,
        sharePercent: Number(share),
        purpose: purpose.trim() || null,
      },
      t("proposed"),
    );

  if (!view.available) {
    return <p role="status">{t("unavailable")}</p>;
  }

  return (
    <div className="agents">
      {view.canPropose && champion !== null ? (
        <section className="agents-section" aria-labelledby={`${ids}-propose`}>
          <h2 id={`${ids}-propose`}>{t("propose")}</h2>
          <p>{t("proposeHint", { version: String(champion) })}</p>
          <fieldset className="agents-checks">
            <legend>{t("kind")}</legend>
            <label>
              <input type="radio" name={`${ids}-kind`} checked={kind === "version"} onChange={() => setKind("version")} disabled={challengers.length === 0} />
              {t("kinds.version")}
            </label>
            <label>
              <input type="radio" name={`${ids}-kind`} checked={kind === "model"} onChange={() => setKind("model")} />
              {t("kinds.model")}
            </label>
          </fieldset>
          <div className="agents-row">
            {kind === "version" ? (
              <div className="field">
                <label htmlFor={`${ids}-version`}>{t("challengerVersion")}</label>
                <select id={`${ids}-version`} value={version} onChange={(e) => setVersion(e.target.value)}>
                  {challengers.map((v) => (
                    <option key={v} value={v}>
                      {te("dashboard.versionNumber", { number: String(v) })}
                    </option>
                  ))}
                </select>
              </div>
            ) : (
              <div className="field">
                <label htmlFor={`${ids}-model`}>{t("challengerModel")}</label>
                <input id={`${ids}-model`} type="text" dir="ltr" value={model} onChange={(e) => setModel(e.target.value)} />
                <p className="field-hint">{t("modelHint")}</p>
              </div>
            )}
            <div className="field">
              <label htmlFor={`${ids}-share`}>{t("share")}</label>
              <input id={`${ids}-share`} type="number" min={1} max={50} step="1" value={share} onChange={(e) => setShare(e.target.value)} />
            </div>
          </div>
          <div className="field">
            <label htmlFor={`${ids}-purpose`}>{t("purpose")}</label>
            <textarea id={`${ids}-purpose`} dir="auto" rows={2} maxLength={500} value={purpose} onChange={(e) => setPurpose(e.target.value)} />
          </div>
          <div className="agents-row">
            <Button variant="primary" busy={busy === "propose"} busyLabel={te("working")} onClick={() => void propose()} data-testid="eval-comparison-propose">
              {t("proposeAction")}
            </Button>
          </div>
        </section>
      ) : null}
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <section className="agents-section" aria-labelledby={`${ids}-list`}>
        <h2 id={`${ids}-list`}>{t("title")}</h2>
        {view.comparisons.length === 0 ? <p>{t("empty")}</p> : null}
        {view.comparisons.map((c) => {
          const rows = [...new Set(c.arms.flatMap((a) => a.metrics.map((m) => `${m.key}:${m.locale}`)))].sort();
          return (
            <article key={c.id} className="agents-card" data-testid="eval-comparison" aria-labelledby={`${ids}-${c.id}`}>
              <h3 id={`${ids}-${c.id}`}>
                {c.kind === "model"
                  ? t("titleModel", { version: String(c.championVersion), model: c.challengerModel ?? "" })
                  : t("titleVersion", { champion: String(c.championVersion), challenger: String(c.challengerVersion) })}
              </h3>
              <p className="agents-row">
                <span className="agents-badge">{t(`statuses.${c.status}`)}</span>
                <span>{t("shareValue", { share: format.number(c.sharePercent / 100, { style: "percent", maximumFractionDigits: 1 }) })}</span>
                <span>
                  {t("gate")} {te.has(`gate.${c.gateStatus}`) ? te(`gate.${c.gateStatus}`) : c.gateStatus}
                </span>
                <span>{format.dateTime(new Date(c.proposedAt), { dateStyle: "medium" })}</span>
              </p>
              {c.purpose ? <p dir="auto">{c.purpose}</p> : null}
              {c.stopReason ? <p>{t(`stopReasons.${c.stopReason}`)}</p> : null}
              {c.decisionNote ? <p dir="auto">{t("note", { note: c.decisionNote })}</p> : null}
              {c.arms.length > 0 ? (
                <div className="agents-table">
                  <table>
                    <caption>{t("armsCaption")}</caption>
                    <thead>
                      <tr>
                        <th scope="col">{te("metric")}</th>
                        <th scope="col">{te("locale")}</th>
                        {c.arms.map((a) => (
                          <th scope="col" key={a.arm}>
                            {t("armSamples", { arm: t(`arms.${a.arm}`), samples: String(a.samples) })}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((r) => {
                        const [key = "", locale = ""] = r.split(":");
                        return (
                          <tr key={r}>
                            <th scope="row">{label(key)}</th>
                            <td>{te(`locales.${locale}`)}</td>
                            {c.arms.map((a) => {
                              const m = a.metrics.find((x) => x.key === key && x.locale === locale);
                              return (
                                <td key={a.arm} data-number>
                                  {m ? percent(m.value) : te("none")}
                                </td>
                              );
                            })}
                          </tr>
                        );
                      })}
                      <tr>
                        <th scope="row" colSpan={2}>
                          {t("latency")}
                        </th>
                        {c.arms.map((a) => (
                          <td key={a.arm} data-number>
                            {a.latencyP95Ms === null ? te("none") : te("ms", { value: format.number(Math.round(a.latencyP95Ms)) })}
                          </td>
                        ))}
                      </tr>
                      <tr>
                        <th scope="row" colSpan={2}>
                          {t("denials")}
                        </th>
                        {c.arms.map((a) => (
                          <td key={a.arm} data-number>
                            {format.number(a.denials)}
                          </td>
                        ))}
                      </tr>
                    </tbody>
                  </table>
                </div>
              ) : null}
              {c.canDecide ? (
                <div className="agents-row">
                  <div className="field">
                    <label htmlFor={`${ids}-${c.id}-note`}>{t("decisionNote")}</label>
                    <input
                      id={`${ids}-${c.id}-note`}
                      type="text"
                      dir="auto"
                      maxLength={500}
                      value={notes[c.id] ?? ""}
                      onChange={(e) => setNotes((current) => ({ ...current, [c.id]: e.target.value }))}
                    />
                  </div>
                  <Button
                    variant="primary"
                    busy={busy === `${c.id}:approve`}
                    busyLabel={te("working")}
                    onClick={() => void act(`${c.id}:approve`, `/api/v1/evaluation/comparisons/${c.id}/decision`, { approve: true, note: notes[c.id] || null }, t("approved"))}
                  >
                    {t("approve")}
                  </Button>
                  <Button
                    busy={busy === `${c.id}:reject`}
                    busyLabel={te("working")}
                    onClick={() => void act(`${c.id}:reject`, `/api/v1/evaluation/comparisons/${c.id}/decision`, { approve: false, note: notes[c.id] || null }, t("rejected"))}
                  >
                    {t("reject")}
                  </Button>
                </div>
              ) : null}
              {c.canStop ? (
                <div className="agents-row">
                  <Button busy={busy === `${c.id}:stop`} busyLabel={te("working")} onClick={() => void act(`${c.id}:stop`, `/api/v1/evaluation/comparisons/${c.id}/stop`, undefined, t("stoppedDone"))}>
                    {c.status === "running" ? t("stop") : t("withdraw")}
                  </Button>
                </div>
              ) : null}
            </article>
          );
        })}
      </section>
    </div>
  );
}
