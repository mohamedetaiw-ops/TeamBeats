"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";
import { pickText } from "@/lib/agents/types";
import { REVIEW_VERDICTS, SEVERITIES, type ReviewItemView } from "@/lib/evaluation/types";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * A reviewer's decision on a flagged production sample (spec 034 US8 scenarios 2 and 3): a verdict per judged metric,
 * defaulting to the judge's, with a note, and once reviewed, adding the case to one of the agent's datasets. The API
 * redacts the texts again and decides who may see the content or promote it.
 */
export function ReviewDecision({ initial, locale }: { initial: ReviewItemView; locale: string }) {
  const t = useTranslations("evaluation.review");
  const te = useTranslations("evaluation");
  const ids = useId();
  const [view, setView] = useState(initial);
  const [verdicts, setVerdicts] = useState<Record<string, string>>(() => Object.fromEntries(Object.entries(initial.judge).map(([k, v]) => [k, v.verdict])));
  const [note, setNote] = useState("");
  const [dataset, setDataset] = useState(initial.datasets[0]?.id ?? "");
  const [input, setInput] = useState(initial.content?.question ?? "");
  const [expected, setExpected] = useState("");
  const [severity, setSeverity] = useState("warning");
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);
  const label = (key: string) => (te.has(`metrics.${key}`) ? te(`metrics.${key}`) : key);
  const judged = Object.entries(view.judge);

  async function post(key: string, path: string, body: unknown, done: string) {
    setBusy(key);
    setResult(null);
    const sent = await send<ReviewItemView>(path, "POST", body, te("failed"));
    setBusy(null);
    if (sent.ok) {
      setView(sent.data);
      setResult({ ok: true, messages: [done] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  return (
    <div className="agents">
      <section className="agents-section" aria-labelledby={`${ids}-verdicts`}>
        <h2 id={`${ids}-verdicts`}>{t("verdicts")}</h2>
        {judged.length === 0 ? <p>{t("noJudged")}</p> : null}
        {judged.length > 0 ? (
          <div className="agents-table">
            <table data-testid="eval-review-verdicts">
              <thead>
                <tr>
                  <th scope="col">{te("metric")}</th>
                  <th scope="col">{t("judge")}</th>
                  <th scope="col">{t("reviewer")}</th>
                </tr>
              </thead>
              <tbody>
                {judged.map(([key, v]) => {
                  const decided = view.verdicts[key];
                  return (
                    <tr key={key}>
                      <th scope="row">{label(key)}</th>
                      <td>
                        {te(`judgeVerdict.${v.verdict}`)}
                        {v.reason ? (
                          <>
                            {" "}
                            <span dir="auto">{v.reason}</span>
                          </>
                        ) : null}
                      </td>
                      <td>
                        {view.status === "open" ? (
                          <select
                            aria-label={t("verdictFor", { metric: label(key) })}
                            value={verdicts[key] ?? v.verdict}
                            onChange={(e) => setVerdicts((current) => ({ ...current, [key]: e.target.value }))}
                          >
                            {REVIEW_VERDICTS.map((r) => (
                              <option key={r} value={r}>
                                {te(`judgeVerdict.${r}`)}
                              </option>
                            ))}
                          </select>
                        ) : decided ? (
                          <>
                            {te(`judgeVerdict.${decided.reviewer}`)}
                            {decided.reviewer !== decided.judge ? <span className="agents-badge">{t("overturned")}</span> : null}
                          </>
                        ) : (
                          te("none")
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        ) : null}
        {view.status === "open" ? (
          <>
            <div className="field">
              <label htmlFor={`${ids}-note`}>{t("note")}</label>
              <textarea id={`${ids}-note`} dir="auto" rows={2} maxLength={1000} value={note} onChange={(e) => setNote(e.target.value)} />
            </div>
            <div className="agents-row">
              <Button
                variant="primary"
                busy={busy === "decide"}
                busyLabel={te("working")}
                data-testid="eval-review-decide"
                onClick={() => void post("decide", `/api/v1/evaluation/review/${view.id}/decision`, { verdicts, note: note.trim() || null }, t("decided"))}
              >
                {t("decide")}
              </Button>
            </div>
          </>
        ) : view.note ? (
          <p dir="auto">{t("noteValue", { note: view.note })}</p>
        ) : null}
      </section>
      {view.promotedDatasetId ? (
        <p role="status" data-testid="eval-review-promoted">
          {t("promotedTo", { version: String(view.promotedVersion ?? "") })}
        </p>
      ) : null}
      {view.canPromote ? (
        <section className="agents-section" aria-labelledby={`${ids}-promote`}>
          <h2 id={`${ids}-promote`}>{t("promote")}</h2>
          <p>{t("promoteHint")}</p>
          {view.datasets.length === 0 ? (
            <p>{t("noDatasets")}</p>
          ) : (
            <>
              <div className="agents-row">
                <div className="field">
                  <label htmlFor={`${ids}-dataset`}>{t("dataset")}</label>
                  <select id={`${ids}-dataset`} value={dataset} onChange={(e) => setDataset(e.target.value)}>
                    {view.datasets.map((d) => (
                      <option key={d.id} value={d.id}>
                        {pickText(d.names, locale) || te("untitled")}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="field">
                  <label htmlFor={`${ids}-severity`}>{te("run.severity")}</label>
                  <select id={`${ids}-severity`} value={severity} onChange={(e) => setSeverity(e.target.value)}>
                    {SEVERITIES.map((s) => (
                      <option key={s} value={s}>
                        {te(`severity.${s}`)}
                      </option>
                    ))}
                  </select>
                </div>
              </div>
              <div className="field">
                <label htmlFor={`${ids}-input`}>{t("input")}</label>
                <textarea id={`${ids}-input`} dir="auto" rows={3} value={input} onChange={(e) => setInput(e.target.value)} />
              </div>
              <div className="field">
                <label htmlFor={`${ids}-expected`}>{t("expected")}</label>
                <textarea id={`${ids}-expected`} dir="auto" rows={3} value={expected} onChange={(e) => setExpected(e.target.value)} />
                <p className="field-hint">{t("expectedHint")}</p>
              </div>
              <div className="agents-row">
                <Button
                  variant="primary"
                  busy={busy === "promote"}
                  busyLabel={te("working")}
                  data-testid="eval-review-promote"
                  onClick={() =>
                    void post(
                      "promote",
                      `/api/v1/evaluation/review/${view.id}/promote`,
                      { datasetId: dataset || null, input: input.trim() || null, expectedAnswer: expected.trim() || null, keyFacts: null, severity },
                      t("promoted"),
                    )
                  }
                >
                  {t("promoteAction")}
                </Button>
              </div>
            </>
          )}
        </section>
      ) : null}
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
