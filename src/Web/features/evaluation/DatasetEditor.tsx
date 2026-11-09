"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { pickText } from "@/lib/agents/types";
import { send } from "@/lib/evaluation/send";
import {
  CLASSIFICATIONS,
  LOCALES,
  SEVERITIES,
  type CaseInput,
  type CaseView,
  type DatasetView,
  type ExpectedSource,
  type ImportPreview,
  type Persona,
} from "@/lib/evaluation/types";

type Draft = {
  input: string;
  locale: string;
  personaId: string;
  severity: string;
  classification: string;
  tags: string;
  answer: string;
  keyFacts: string;
  sources: string;
  forbiddenSources: string;
  tools: string;
  forbiddenTools: string;
  toolOrder: boolean;
  refusal: boolean;
  escalation: boolean;
};

const EMPTY: Draft = {
  input: "",
  locale: "en",
  personaId: "",
  severity: "blocking",
  classification: "internal",
  tags: "",
  answer: "",
  keyFacts: "",
  sources: "",
  forbiddenSources: "",
  tools: "",
  forbiddenTools: "",
  toolOrder: false,
  refusal: false,
  escalation: false,
};

const lines = (text: string) =>
  text
    .split("\n")
    .map((l) => l.trim())
    .filter(Boolean);

const sourceLines = (text: string): ExpectedSource[] =>
  lines(text).map((l) => {
    const at = l.lastIndexOf(":");
    return at < 0 ? { source: l, itemId: null } : { source: l.slice(0, at), itemId: l.slice(at + 1) };
  });

function toDraft(c: CaseInput): Draft {
  const e = c.expect;
  return {
    input: c.input,
    locale: c.locale,
    personaId: c.personaId ?? "",
    severity: c.severity,
    classification: c.classification,
    tags: c.tags.join(", "),
    answer: e.answer ?? "",
    keyFacts: (e.keyFacts ?? []).join("\n"),
    sources: (e.sources ?? []).map((s) => `${s.source}:${s.itemId}`).join("\n"),
    forbiddenSources: (e.forbiddenSources ?? []).map((s) => `${s.source}:${s.itemId}`).join("\n"),
    tools: (e.tools ?? []).map((x) => x.tool ?? "").join("\n"),
    forbiddenTools: (e.forbiddenTools ?? []).join("\n"),
    toolOrder: e.toolOrder,
    refusal: e.refusal,
    escalation: e.escalation,
  };
}

function toCase(d: Draft, previous?: CaseInput): CaseInput {
  // Parameters of expected tools are kept from the case as it was; the editor changes the tool list only.
  const kept = new Map((previous?.expect.tools ?? []).map((x) => [x.tool, x.parameters]));
  return {
    input: d.input,
    locale: d.locale,
    personaId: d.personaId === "" ? null : d.personaId,
    severity: d.severity,
    classification: d.classification,
    tags: d.tags
      .split(",")
      .map((x) => x.trim())
      .filter(Boolean),
    expect: {
      answer: d.answer.trim() === "" ? null : d.answer,
      keyFacts: lines(d.keyFacts),
      sources: sourceLines(d.sources),
      forbiddenSources: sourceLines(d.forbiddenSources),
      tools: lines(d.tools).map((tool) => ({ tool, parameters: kept.get(tool) ?? null })),
      forbiddenTools: lines(d.forbiddenTools),
      toolOrder: d.toolOrder,
      refusal: d.refusal,
      escalation: d.escalation,
    },
  };
}

function strip(c: CaseView): CaseInput {
  const { n: _n, ...rest } = c;
  return rest;
}

/**
 * The dataset editor (spec 034 US2): the cases of the current version as a table, a form to add or change a case with
 * its persona and expectations, and a CSV or JSON import that lists refused rows and imports nothing until confirmed.
 * Every save makes a new version, so versions used as evidence stay as they were. The API validates each case.
 */
export function DatasetEditor({ initial, personas, latest }: { initial: DatasetView; personas: Persona[]; latest: boolean }) {
  const t = useTranslations("evaluation");
  const locale = useLocale();
  const ids = useId();
  const [view, setView] = useState(initial);
  const [cases, setCases] = useState<CaseInput[]>(initial.cases.map(strip));
  const [editing, setEditing] = useState<number | null>(null);
  const [draft, setDraft] = useState<Draft>(EMPTY);
  const [format, setFormat] = useState<"csv" | "json">("csv");
  const [content, setContent] = useState("");
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const editable = view.canEdit && latest;
  const personaName = (id: string | null) => {
    const p = id ? personas.find((x) => x.id === id) : undefined;
    return p ? pickText(p.names, locale) : t("dataset.defaultPersona");
  };

  function field<K extends keyof Draft>(key: K, value: Draft[K]) {
    setDraft((current) => ({ ...current, [key]: value }));
  }

  async function persist(next: CaseInput[]) {
    setBusy("save");
    setResult(null);
    const sent = await send<DatasetView>(`/api/v1/evaluation/datasets/${view.id}/cases`, "PUT", { version: view.version, cases: next }, t("failed"));
    setBusy(null);
    if (sent.ok) {
      setView(sent.data);
      setCases(sent.data.cases.map(strip));
      setEditing(null);
      setDraft(EMPTY);
      setResult({ ok: true, messages: [t("dataset.saved", { version: String(sent.data.version) })] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  function keep() {
    const next = editing === null ? [...cases, toCase(draft)] : cases.map((c, i) => (i === editing ? toCase(draft, c) : c));
    void persist(next);
  }

  async function check() {
    setBusy("check");
    setResult(null);
    setPreview(null);
    const sent = await send<ImportPreview>(`/api/v1/evaluation/datasets/${view.id}/import`, "POST", { format, content }, t("failed"));
    setBusy(null);
    if (sent.ok) setPreview(sent.data);
    else setResult({ ok: false, messages: sent.messages });
  }

  async function confirm() {
    if (!preview) return;
    setBusy("confirm");
    setResult(null);
    const sent = await send<DatasetView>(`/api/v1/evaluation/datasets/${view.id}/imports/${preview.importId}/confirm`, "POST", undefined, t("failed"));
    setBusy(null);
    if (sent.ok) {
      setView(sent.data);
      setCases(sent.data.cases.map(strip));
      setPreview(null);
      setContent("");
      setResult({ ok: true, messages: [t("dataset.imported", { version: String(sent.data.version) })] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  async function readFile(file: File | undefined) {
    if (!file) return;
    setContent(await file.text());
    setFormat(file.name.toLowerCase().endsWith(".json") ? "json" : "csv");
  }

  const area = (key: "keyFacts" | "sources" | "forbiddenSources" | "tools" | "forbiddenTools") => (
    <div className="field">
      <label htmlFor={`${ids}-${key}`}>{t(`case.expect.${key}`)}</label>
      <p id={`${ids}-${key}-hint`} className="field-hint">
        {t(`dataset.hints.${key}`)}
      </p>
      <textarea id={`${ids}-${key}`} aria-describedby={`${ids}-${key}-hint`} dir="auto" rows={3} value={draft[key]} onChange={(e) => field(key, e.target.value)} />
    </div>
  );

  return (
    <div className="agents">
      <section className="agents-section" aria-labelledby={`${ids}-cases`}>
        <h2 id={`${ids}-cases`}>{t("dataset.cases", { version: String(view.version) })}</h2>
        {!latest ? <p className="notice">{t("dataset.oldVersion")}</p> : null}
        {cases.length === 0 ? (
          <p>{t("dataset.noCases")}</p>
        ) : (
          <div className="agents-table">
            <table data-testid="eval-dataset-cases">
              <thead>
                <tr>
                  <th scope="col">{t("run.case")}</th>
                  <th scope="col">{t("run.input")}</th>
                  <th scope="col">{t("locale")}</th>
                  <th scope="col">{t("case.persona")}</th>
                  <th scope="col">{t("run.severity")}</th>
                  <th scope="col">{t("dataset.tags")}</th>
                  {editable ? <th scope="col">{t("suite.actions")}</th> : null}
                </tr>
              </thead>
              <tbody>
                {cases.map((c, i) => (
                  <tr key={`${i}:${c.input}`}>
                    <th scope="row">{t("run.caseNumber", { n: String(i + 1) })}</th>
                    <td>
                      <bdi>{c.input}</bdi>
                    </td>
                    <td>{t(`locales.${c.locale}`)}</td>
                    <td>
                      <bdi>{personaName(c.personaId)}</bdi>
                    </td>
                    <td>{t(`severity.${c.severity}`)}</td>
                    <td>
                      <bdi dir="ltr">{c.tags.join(", ")}</bdi>
                    </td>
                    {editable ? (
                      <td>
                        <div className="agents-row">
                          <Button
                            variant="quiet"
                            onClick={() => {
                              setEditing(i);
                              setDraft(toDraft(c));
                            }}
                          >
                            {t("dataset.edit", { n: String(i + 1) })}
                          </Button>
                          <Button variant="quiet" busy={busy === "save"} onClick={() => void persist(cases.filter((_, j) => j !== i))}>
                            {t("dataset.remove", { n: String(i + 1) })}
                          </Button>
                        </div>
                      </td>
                    ) : null}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      {editable ? (
        <section className="agents-section" aria-labelledby={`${ids}-form`}>
          <h2 id={`${ids}-form`}>{editing === null ? t("dataset.add") : t("dataset.change", { n: String(editing + 1) })}</h2>
          <div className="field">
            <label htmlFor={`${ids}-input`}>{t("case.input")}</label>
            <textarea id={`${ids}-input`} dir="auto" rows={3} value={draft.input} onChange={(e) => field("input", e.target.value)} />
          </div>
          <div className="agents-row">
            <div className="field">
              <label htmlFor={`${ids}-locale`}>{t("locale")}</label>
              <select id={`${ids}-locale`} value={draft.locale} onChange={(e) => field("locale", e.target.value)}>
                {LOCALES.map((l) => (
                  <option key={l} value={l}>
                    {t(`locales.${l}`)}
                  </option>
                ))}
              </select>
            </div>
            <div className="field">
              <label htmlFor={`${ids}-persona`}>{t("case.persona")}</label>
              <select id={`${ids}-persona`} value={draft.personaId} onChange={(e) => field("personaId", e.target.value)}>
                <option value="">{t("dataset.defaultPersona")}</option>
                {personas.map((p) => (
                  <option key={p.id} value={p.id}>
                    {pickText(p.names, locale)}
                  </option>
                ))}
              </select>
            </div>
            <div className="field">
              <label htmlFor={`${ids}-severity`}>{t("run.severity")}</label>
              <select id={`${ids}-severity`} value={draft.severity} onChange={(e) => field("severity", e.target.value)}>
                {SEVERITIES.map((s) => (
                  <option key={s} value={s}>
                    {t(`severity.${s}`)}
                  </option>
                ))}
              </select>
            </div>
            <div className="field">
              <label htmlFor={`${ids}-classification`}>{t("dataset.classification")}</label>
              <select id={`${ids}-classification`} value={draft.classification} onChange={(e) => field("classification", e.target.value)}>
                {CLASSIFICATIONS.map((c) => (
                  <option key={c} value={c}>
                    {t(`classification.${c}`)}
                  </option>
                ))}
              </select>
            </div>
            <div className="field">
              <label htmlFor={`${ids}-tags`}>{t("dataset.tags")}</label>
              <input id={`${ids}-tags`} type="text" dir="ltr" value={draft.tags} onChange={(e) => field("tags", e.target.value)} />
            </div>
          </div>
          <fieldset className="agents-checks">
            <legend>{t("case.expected")}</legend>
            <div className="field">
              <label htmlFor={`${ids}-answer`}>{t("case.expect.answer")}</label>
              <textarea id={`${ids}-answer`} dir="auto" rows={3} value={draft.answer} onChange={(e) => field("answer", e.target.value)} />
            </div>
            <div className="agents-pair">
              {area("keyFacts")}
              {area("tools")}
              {area("forbiddenTools")}
              {area("sources")}
              {area("forbiddenSources")}
            </div>
            {(["toolOrder", "refusal", "escalation"] as const).map((key) => (
              <label key={key}>
                <input type="checkbox" checked={draft[key]} onChange={(e) => field(key, e.target.checked)} />
                {t(`case.${key}`)}
              </label>
            ))}
          </fieldset>
          <div className="agents-row">
            <Button variant="primary" busy={busy === "save"} busyLabel={t("working")} onClick={keep} data-testid="eval-case-save">
              {editing === null ? t("dataset.addButton") : t("dataset.saveCase")}
            </Button>
            {editing !== null ? (
              <Button
                onClick={() => {
                  setEditing(null);
                  setDraft(EMPTY);
                }}
              >
                {t("dataset.cancelEdit")}
              </Button>
            ) : null}
          </div>
        </section>
      ) : null}
      {editable ? (
        <section className="agents-section" aria-labelledby={`${ids}-import`}>
          <h2 id={`${ids}-import`}>{t("dataset.import")}</h2>
          <p>{t("dataset.importHint")}</p>
          <div className="agents-row">
            <div className="field">
              <label htmlFor={`${ids}-format`}>{t("dataset.format")}</label>
              <select id={`${ids}-format`} value={format} onChange={(e) => setFormat(e.target.value === "json" ? "json" : "csv")}>
                <option value="csv">{t("dataset.csv")}</option>
                <option value="json">{t("dataset.json")}</option>
              </select>
            </div>
            <div className="field">
              <label htmlFor={`${ids}-file`}>{t("dataset.file")}</label>
              <input id={`${ids}-file`} type="file" accept=".csv,.json,text/csv,application/json" onChange={(e) => void readFile(e.target.files?.[0])} />
            </div>
          </div>
          <div className="field">
            <label htmlFor={`${ids}-content`}>{t("dataset.content")}</label>
            <textarea id={`${ids}-content`} dir="ltr" rows={6} value={content} onChange={(e) => setContent(e.target.value)} />
          </div>
          <div className="agents-row">
            <Button busy={busy === "check"} busyLabel={t("working")} onClick={() => void check()} data-testid="eval-import-check">
              {t("dataset.check")}
            </Button>
          </div>
          {preview ? (
            <div className="agents-section" data-testid="eval-import-preview">
              <h3>{t("dataset.preview")}</h3>
              <p>{t("dataset.previewSummary", { valid: String(preview.valid), refused: String(preview.errors.length) })}</p>
              {preview.errors.length > 0 ? (
                <div className="agents-table">
                  <table>
                    <thead>
                      <tr>
                        <th scope="col">{t("dataset.row")}</th>
                        <th scope="col">{t("dataset.field")}</th>
                        <th scope="col">{t("dataset.reason")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {preview.errors.map((e) => (
                        <tr key={`${e.row}:${e.field}:${e.key}`}>
                          <td data-number>{e.row === 0 ? t("dataset.wholeFile") : e.row}</td>
                          <td>
                            <bdi dir="ltr">{e.field}</bdi>
                          </td>
                          <td>{e.message ?? e.key}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
              <div className="agents-row">
                <Button variant="primary" disabled={preview.valid === 0} busy={busy === "confirm"} busyLabel={t("working")} onClick={() => void confirm()} data-testid="eval-import-confirm">
                  {t("dataset.confirm", { count: String(preview.valid) })}
                </Button>
                <Button onClick={() => setPreview(null)}>{t("dataset.discard")}</Button>
              </div>
            </div>
          ) : null}
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
