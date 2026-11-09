"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";
import { LOCALES } from "@/lib/evaluation/types";

/** Starts a calibration of one rubric (spec 034 US8): the judge reads every label and its agreement is measured per language. */
export function CalibrationStart({ metric, running }: { metric: string; running: boolean }) {
  const t = useTranslations("evaluation");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function start() {
    setBusy(true);
    setMessages([]);
    const sent = await send(`/api/v1/evaluation/calibration/${metric}/runs`, "POST", undefined, t("failed"));
    setBusy(false);
    if (sent.ok) router.refresh();
    else setMessages(sent.messages);
  }

  return (
    <div className="agents-row">
      <Button disabled={running} busy={busy} busyLabel={t("working")} onClick={() => void start()} data-testid={`eval-calibrate-${metric}`}>
        {running ? t("calibration.running") : t("calibration.start")}
      </Button>
      {messages.length > 0 ? (
        <div role="alert" className="notice notice-error">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/** Adds a person's verdict on an answer to a rubric's calibration set, or removes one; the API checks who may label. */
export function CalibrationLabels({
  metric,
  labels,
  canLabel,
}: {
  metric: string;
  labels: { id: string; locale: string; input: string; answer: string; label: string }[];
  canLabel: boolean;
}) {
  const t = useTranslations("evaluation");
  const router = useRouter();
  const ids = useId();
  const [locale, setLocale] = useState<string>("en");
  const [input, setInput] = useState("");
  const [expected, setExpected] = useState("");
  const [facts, setFacts] = useState("");
  const [answer, setAnswer] = useState("");
  const [label, setLabel] = useState("pass");
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);

  async function add() {
    setBusy("add");
    setResult(null);
    const keyFacts = facts
      .split("\n")
      .map((f) => f.trim())
      .filter(Boolean);
    const sent = await send(`/api/v1/evaluation/calibration/${metric}/labels`, "POST", { locale, input, expectedAnswer: expected, keyFacts, answer, label }, t("failed"));
    setBusy(null);
    if (sent.ok) {
      setInput("");
      setExpected("");
      setFacts("");
      setAnswer("");
      setResult({ ok: true, messages: [t("calibration.added")] });
      router.refresh();
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  async function remove(id: string) {
    setBusy(id);
    setResult(null);
    const sent = await send(`/api/v1/evaluation/calibration/labels/${id}`, "DELETE", undefined, t("failed"));
    setBusy(null);
    if (sent.ok) router.refresh();
    else setResult({ ok: false, messages: sent.messages });
  }

  return (
    <>
      {labels.length === 0 ? (
        <p>{t("calibration.noLabels")}</p>
      ) : (
        <div className="agents-table">
          <table data-testid="eval-calibration-labels">
            <thead>
              <tr>
                <th scope="col">{t("locale")}</th>
                <th scope="col">{t("case.input")}</th>
                <th scope="col">{t("case.answer")}</th>
                <th scope="col">{t("calibration.label")}</th>
                {canLabel ? <th scope="col">{t("suite.actions")}</th> : null}
              </tr>
            </thead>
            <tbody>
              {labels.map((l) => (
                <tr key={l.id}>
                  <td>{t(`locales.${l.locale}`)}</td>
                  <td>
                    <bdi>{l.input}</bdi>
                  </td>
                  <td>
                    <bdi>{l.answer}</bdi>
                  </td>
                  <td>{t(`judgeVerdict.${l.label}`)}</td>
                  {canLabel ? (
                    <td>
                      <Button variant="quiet" busy={busy === l.id} busyLabel={t("working")} onClick={() => void remove(l.id)}>
                        {t("calibration.remove")}
                      </Button>
                    </td>
                  ) : null}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {canLabel ? (
      <fieldset className="agents-checks">
        <legend>{t("calibration.add")}</legend>
        <div className="agents-row">
          <div className="field">
            <label htmlFor={`${ids}-locale`}>{t("locale")}</label>
            <select id={`${ids}-locale`} value={locale} onChange={(e) => setLocale(e.target.value)}>
              {LOCALES.map((l) => (
                <option key={l} value={l}>
                  {t(`locales.${l}`)}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor={`${ids}-label`}>{t("calibration.label")}</label>
            <select id={`${ids}-label`} value={label} onChange={(e) => setLabel(e.target.value)}>
              <option value="pass">{t("judgeVerdict.pass")}</option>
              <option value="fail">{t("judgeVerdict.fail")}</option>
            </select>
          </div>
        </div>
        <div className="field">
          <label htmlFor={`${ids}-input`}>{t("case.input")}</label>
          <textarea id={`${ids}-input`} dir="auto" rows={2} value={input} onChange={(e) => setInput(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={`${ids}-expected`}>{t("case.expect.answer")}</label>
          <textarea id={`${ids}-expected`} dir="auto" rows={2} value={expected} onChange={(e) => setExpected(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={`${ids}-facts`}>{t("case.expect.keyFacts")}</label>
          <p id={`${ids}-facts-hint`} className="field-hint">
            {t("dataset.hints.keyFacts")}
          </p>
          <textarea id={`${ids}-facts`} aria-describedby={`${ids}-facts-hint`} dir="auto" rows={2} value={facts} onChange={(e) => setFacts(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={`${ids}-answer`}>{t("calibration.answer")}</label>
          <textarea id={`${ids}-answer`} dir="auto" rows={3} value={answer} onChange={(e) => setAnswer(e.target.value)} />
        </div>
        <div className="agents-row">
          <Button variant="primary" busy={busy === "add"} busyLabel={t("working")} onClick={() => void add()} data-testid="eval-label-add">
            {t("calibration.addButton")}
          </Button>
        </div>
      </fieldset>
      ) : null}
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </>
  );
}
