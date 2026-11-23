"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { nameIn, type CheckQuestions, type Names, type VersionView } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import "./policies.css";

type Draft = { clauseKey: string; prompts: Names; options: { key: string; texts: Names; right: boolean }[]; multiple: boolean };

const keys = ["a", "b", "c", "d", "e", "f"];

/**
 * Writes the question bank of one version (spec 025 US6): each question linked to a clause, its prompt and two to six
 * answers in every language of the version, the right answers marked. The API checks the bank and keeps it locked
 * while an open campaign asks it.
 */
export function QuestionsEditor({ policyId, version, initial }: { policyId: string; version: VersionView; initial: CheckQuestions }) {
  const t = useTranslations("policies.questions");
  const locale = useLocale();
  const ids = useId();
  const [questions, setQuestions] = useState<Draft[]>(
    initial.questions.map((q) => ({
      clauseKey: q.clauseKey,
      prompts: q.prompts,
      multiple: q.multiple,
      options: q.options.map((o) => ({ key: o.key, texts: o.texts, right: q.correct?.includes(o.key) ?? false })),
    })),
  );
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const clauses = version.clauses.map((c) => ({ value: c.key, label: `${c.number} ${nameIn(c.titles, locale)}` }));
  const empty = (): Draft => ({
    clauseKey: version.clauses[0]?.key ?? "",
    prompts: {},
    multiple: false,
    options: [
      { key: "a", texts: {}, right: true },
      { key: "b", texts: {}, right: false },
    ],
  });
  const change = (i: number, next: Partial<Draft>) => setQuestions((all) => all.map((q, j) => (j === i ? { ...q, ...next } : q)));

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/drafts/${encodeURIComponent(version.id)}/questions`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          questions: questions.map((q) => ({
            clauseKey: q.clauseKey,
            prompts: q.prompts,
            multiple: q.multiple,
            options: q.options.map((o) => ({ key: o.key, texts: o.texts })),
            correct: q.options.filter((o) => o.right).map((o) => o.key),
          })),
        }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      setSaved(true);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (initial.locked) {
    return (
      <section className="policies-card" data-testid="questions-locked">
        <p>{t("locked", { count: initial.questions.length })}</p>
      </section>
    );
  }

  return (
    <form onSubmit={(e) => void save(e)} noValidate data-testid="questions-editor" aria-labelledby={`${ids}-title`}>
      <h2 id={`${ids}-title`} className="visually-hidden">
        {t("title")}
      </h2>
      <p className="policies-meta">{t(version.number ? "forVersion" : "forDraft", { number: version.number ?? 0 })}</p>
      {questions.length === 0 ? <p className="policies-meta">{t("none")}</p> : null}
      {questions.map((q, i) => (
        <fieldset key={i} className="policies-card" data-testid="question-card">
          <legend>{t("question", { number: i + 1 })}</legend>
          <SelectField label={t("clause")} name={`clause-${i}`} value={q.clauseKey} options={clauses} onChange={(e) => change(i, { clauseKey: e.target.value })} />
          {version.locales.map((l) => (
            <FormField
              key={l}
              label={t("prompt", { language: t(`language.${l === "ar" ? "ar" : "en"}`) })}
              name={`prompt-${i}-${l}`}
              lang={l}
              dir={l === "ar" ? "rtl" : "ltr"}
              value={q.prompts[l] ?? ""}
              maxLength={500}
              onChange={(e) => change(i, { prompts: { ...q.prompts, [l]: e.target.value } })}
            />
          ))}
          <Checkbox label={t("multiple")} name={`multiple-${i}`} checked={q.multiple} onChange={(e) => change(i, { multiple: e.target.checked })} />
          {q.options.map((o, k) => (
            <div key={o.key} className="policies-option" data-testid="question-option">
              {version.locales.map((l) => (
                <FormField
                  key={l}
                  label={t("answer", { number: k + 1, language: t(`language.${l === "ar" ? "ar" : "en"}`) })}
                  name={`answer-${i}-${k}-${l}`}
                  lang={l}
                  dir={l === "ar" ? "rtl" : "ltr"}
                  value={o.texts[l] ?? ""}
                  maxLength={300}
                  onChange={(e) => change(i, { options: q.options.map((x, y) => (y === k ? { ...x, texts: { ...x.texts, [l]: e.target.value } } : x)) })}
                />
              ))}
              <Checkbox
                label={t("right", { number: k + 1 })}
                name={`right-${i}-${k}`}
                checked={o.right}
                onChange={(e) =>
                  change(i, { options: q.options.map((x, y) => (y === k ? { ...x, right: e.target.checked } : q.multiple ? x : { ...x, right: false })) })
                }
              />
              {q.options.length > 2 ? (
                <Button size="sm" variant="quiet" onClick={() => change(i, { options: q.options.filter((_, y) => y !== k) })}>
                  {t("removeAnswer", { number: k + 1 })}
                </Button>
              ) : null}
            </div>
          ))}
          <div className="button-row">
            {q.options.length < 6 ? (
              <Button
                size="sm"
                variant="secondary"
                icon="plus"
                onClick={() => change(i, { options: [...q.options, { key: keys.find((x) => q.options.every((o) => o.key !== x)) ?? "f", texts: {}, right: false }] })}
              >
                {t("addAnswer")}
              </Button>
            ) : null}
            <Button size="sm" variant="quiet" icon="trash" onClick={() => setQuestions((all) => all.filter((_, j) => j !== i))}>
              {t("remove", { number: i + 1 })}
            </Button>
          </div>
        </fieldset>
      ))}
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="notice" role="status">
          {t("saved")}
        </p>
      ) : null}
      <div className="button-row">
        {questions.length < 50 ? (
          <Button variant="secondary" icon="plus" onClick={() => setQuestions((all) => [...all, empty()])}>
            {t("add")}
          </Button>
        ) : null}
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
          {t("save")}
        </Button>
      </div>
    </form>
  );
}
