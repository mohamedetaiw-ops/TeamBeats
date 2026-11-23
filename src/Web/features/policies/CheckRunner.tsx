"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { nameIn, policyHref, type CheckQuestion, type CheckResult, type CheckState } from "@/lib/policies/types";
import { Alert } from "@/components/ui/Alert";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { RadioGroup } from "@/components/ui/RadioGroup";
import "./policies.css";

/**
 * Takes a comprehension check (spec 025 US6): every question as a fieldset the keyboard and a screen reader can use,
 * one choice or several as the question says. A failed attempt lists the sections to reread, never the answers.
 */
export function CheckRunner({ policyId, initial }: { policyId: string; initial: CheckState }) {
  const t = useTranslations("policies.check");
  const locale = useLocale();
  const ids = useId();
  const [state, setState] = useState(initial);
  const [chosen, setChosen] = useState<Record<string, string[]>>({});
  const [result, setResult] = useState<CheckResult | null>(initial.current ? null : initial.last);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const base = `/api/v1/policies/${encodeURIComponent(policyId)}/check`;

  async function post<T>(url: string, body: unknown): Promise<T | null> {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return null;
      }
      return (await response.json()) as T;
    } catch {
      setMessages([t("failed")]);
      return null;
    } finally {
      setBusy(false);
    }
  }

  async function start() {
    const next = await post<CheckState>(`${base}/start`, {});
    if (next) {
      setState(next);
      setChosen({});
      setResult(null);
    }
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const current = state.current;
    if (!current) return;
    if (current.questions.some((q) => !(chosen[q.id]?.length))) {
      setMessages([t("answerAll")]);
      return;
    }
    const done = await post<CheckResult>(`${base}/${encodeURIComponent(current.id)}/answers`, { answers: chosen });
    if (done) {
      setResult(done);
      setState((s) => ({ ...s, current: null, due: { ...s.due, used: s.due.used + 1, passed: done.passed, needsFollowUp: done.needsFollowUp } }));
    }
  }

  const toggle = (q: CheckQuestion, key: string, on: boolean) =>
    setChosen((c) => {
      const before = c[q.id] ?? [];
      return { ...c, [q.id]: q.multiple ? (on ? [...before, key] : before.filter((k) => k !== key)) : [key] };
    });

  const left = Math.max(0, state.due.attempts - state.due.used);
  const errors =
    messages.length > 0 ? (
      <div className="notice notice-error" role="alert">
        {messages.map((m) => (
          <p key={m}>{m}</p>
        ))}
      </div>
    ) : null;

  if (state.current) {
    const current = state.current;
    return (
      <form className="policies-card" aria-labelledby={`${ids}-title`} onSubmit={(e) => void submit(e)} noValidate data-testid="check-attempt">
        <h2 id={`${ids}-title`}>{t("attempt", { number: current.attempt, of: state.due.attempts })}</h2>
        {current.questions.map((q, i) => {
          const legend = t("question", { number: i + 1, text: nameIn(q.prompts, locale) });
          return q.multiple ? (
            <fieldset key={q.id} className="ui-choice-group" data-testid="check-question">
              <legend>{legend}</legend>
              <p className="field-hint">{t("several")}</p>
              <div className="ui-choice-list">
                {q.options.map((o) => (
                  <Checkbox
                    key={o.key}
                    label={nameIn(o.texts, locale)}
                    name={`q-${q.id}`}
                    checked={chosen[q.id]?.includes(o.key) ?? false}
                    onChange={(e) => toggle(q, o.key, e.target.checked)}
                  />
                ))}
              </div>
            </fieldset>
          ) : (
            <div key={q.id} data-testid="check-question">
              <RadioGroup
                legend={legend}
                name={`q-${q.id}`}
                options={q.options.map((o) => ({ value: o.key, label: nameIn(o.texts, locale) }))}
                value={chosen[q.id]?.[0] ?? ""}
                onChange={(e) => toggle(q, e.target.value, true)}
              />
            </div>
          );
        })}
        {errors}
        <div className="button-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
            {t("submit")}
          </Button>
        </div>
      </form>
    );
  }

  return (
    <section className="policies-card" aria-labelledby={`${ids}-title`} data-testid="check-result">
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      {result ? (
        result.passed ? (
          <Alert tone="success" live title={t("passed", { score: result.score })}>
            <p>{t("passedHint")}</p>
          </Alert>
        ) : (
          <Alert tone="warning" live title={t("notPassed", { score: result.score, pass: state.due.passPercent })}>
            <p>{t("reread")}</p>
            <ul data-testid="check-reread">
              {result.reread.map((c) => (
                <li key={c.key}>
                  <Link href={`${policyHref(policyId)}#${c.anchor}`}>
                    {c.number} <bdi>{nameIn(c.titles, locale)}</bdi>
                  </Link>
                </li>
              ))}
            </ul>
          </Alert>
        )
      ) : (
        <p>{t("intro", { pass: state.due.passPercent, attempts: state.due.attempts })}</p>
      )}
      {state.due.needsFollowUp ? <p className="field-error">{t("followUp")}</p> : null}
      {errors}
      <div className="button-row">
        {state.due.passed ? (
          <ButtonLink href={`${policyHref(policyId)}?attest=1`} variant="primary" icon="check">
            {t("toAttest")}
          </ButtonLink>
        ) : !state.due.needsFollowUp && left > 0 ? (
          <Button variant="primary" busy={busy} busyLabel={t("busy")} onClick={() => void start()}>
            {t(state.due.used > 0 ? "again" : "start", { left })}
          </Button>
        ) : null}
      </div>
    </section>
  );
}
