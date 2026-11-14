"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { IconButton } from "@/components/ui/IconButton";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { problemMessages } from "@/lib/notifications/types";
import { AGGREGATIONS, HIT_POLICIES, blankDecision, newId, readDecision, writeDecision, type DecisionDraft } from "@/lib/dmn";
import type { DecisionModelView, DecisionTestResult, ModelProblem } from "@/lib/processes";
import "./processes.css";

/**
 * The decision table editor of spec 024 FR-007: names, hit policy, input and output columns and one row per rule, as
 * a plain table of fields. Saving keeps a draft with the problems the server finds; test rows run against the draft;
 * publishing makes the next version, which business rule tasks use from then on. The server decides what is valid.
 */
export function DecisionEditor({ decisionKey, initial, units }: { decisionKey: string | null; initial: DecisionModelView | null; units: OrgUnit[] }) {
  const t = useTranslations("processes.decisions");
  const locale = useLocale();
  const format = useFormatter();
  const [key, setKey] = useState(decisionKey ?? "");
  const [view, setView] = useState(initial);
  const [draft, setDraft] = useState<DecisionDraft>(() => (initial ? readDecision(initial.dmnXml) : null) ?? blankDecision());
  const [owner, setOwner] = useState(initial?.ownerOrgUnitId ?? "");
  const [problems, setProblems] = useState<ModelProblem[]>(initial?.problems ?? []);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [testRows, setTestRows] = useState("");
  const [results, setResults] = useState<{ row: string; result: DecisionTestResult }[] | null>(null);

  const change = (update: (d: DecisionDraft) => DecisionDraft) => setDraft((d) => update(structuredClone(d)));
  const problemFor = (id: string) => problems.filter((p) => p.elementId === id);
  const problemText = (p: ModelProblem) => p.message || p.key;

  async function call(name: string, url: string, init: RequestInit): Promise<Response | null> {
    setBusy(name);
    setMessages([]);
    setNotice(null);
    try {
      const response = await fetch(url, { ...init, headers: { "content-type": "application/json" } });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return null;
      }
      return response;
    } catch {
      setMessages([t("failed")]);
      return null;
    } finally {
      setBusy(null);
    }
  }

  async function save() {
    const response = await call("save", `/api/v1/workflow/decisions/${encodeURIComponent(key.trim())}`, {
      method: "PUT",
      body: JSON.stringify({ dmnXml: writeDecision(key.trim(), draft), ownerOrgUnitId: owner || null }),
    });
    if (!response) return;
    const saved = (await response.json()) as DecisionModelView;
    setView(saved);
    setProblems(saved.problems);
    setNotice(saved.problems.length === 0 ? t("saved") : t("savedWithProblems", { count: saved.problems.length }));
    if (!decisionKey) window.history.replaceState(null, "", `/admin/decisions/${encodeURIComponent(saved.key)}`);
  }

  async function publish() {
    const response = await call("publish", `/api/v1/workflow/decisions/${encodeURIComponent(view!.key)}:publish`, { method: "POST" });
    if (!response) return;
    const published = (await response.json()) as DecisionModelView;
    setView(published);
    setNotice(t("published", { version: String(published.versions[0]?.version ?? 1) }));
  }

  async function test() {
    const lines = testRows.split("\n").map((l) => l.trim()).filter(Boolean);
    let rows: unknown[];
    try {
      rows = lines.map((l) => JSON.parse(l) as unknown);
    } catch {
      setMessages([t("testRowsInvalid")]);
      return;
    }
    const response = await call("test", `/api/v1/workflow/decisions/${encodeURIComponent(view!.key)}:test`, { method: "POST", body: JSON.stringify({ rows }) });
    if (!response) return;
    const list = (await response.json()) as DecisionTestResult[];
    setResults(list.map((result, i) => ({ row: lines[i] ?? "", result })));
  }

  const inputCount = draft.inputs.length;
  const outputCount = draft.outputs.length;

  return (
    <div className="processes">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {problemFor("").length > 0 ? (
        <Alert tone="warning" title={t("problemsTitle")}>
          {problemFor("").map(problemText).join(" ")}
        </Alert>
      ) : null}

      <section className="processes-section" aria-labelledby="decision-settings">
        <h2 id="decision-settings">{decisionKey === null ? t("newTitle") : t("settings")}</h2>
        <div className="processes-filters">
          <FormField
            label={t("key")}
            hint={t("keyHint")}
            name="key"
            value={key}
            onChange={(e) => setKey(e.target.value)}
            readOnly={decisionKey !== null}
            maxLength={100}
            dir="ltr"
          />
          <FormField label={t("nameEn")} name="nameEn" value={draft.names.en} onChange={(e) => change((d) => ({ ...d, names: { ...d.names, en: e.target.value } }))} maxLength={200} />
          <FormField label={t("nameAr")} name="nameAr" dir="rtl" value={draft.names.ar} onChange={(e) => change((d) => ({ ...d, names: { ...d.names, ar: e.target.value } }))} maxLength={200} />
          <SelectField
            label={t("owner")}
            name="owner"
            value={owner}
            onChange={(e) => setOwner(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitName(u, locale) }))]}
          />
          <SelectField
            label={t("hitPolicy")}
            hint={t(`hitPolicyHints.${draft.hitPolicy.replace(" ", "_")}`)}
            name="hitPolicy"
            value={draft.hitPolicy}
            onChange={(e) => change((d) => ({ ...d, hitPolicy: e.target.value as DecisionDraft["hitPolicy"] }))}
            options={HIT_POLICIES.map((p) => ({ value: p, label: t(`hitPolicies.${p.replace(" ", "_")}`) }))}
          />
          {draft.hitPolicy === "COLLECT" ? (
            <SelectField
              label={t("aggregation")}
              name="aggregation"
              value={draft.aggregation}
              onChange={(e) => change((d) => ({ ...d, aggregation: e.target.value as DecisionDraft["aggregation"] }))}
              options={AGGREGATIONS.map((a) => ({ value: a, label: t(`aggregations.${a || "none"}`) }))}
            />
          ) : null}
        </div>
        {problemFor("table").length > 0 ? <p className="field-error">{problemFor("table").map(problemText).join(" ")}</p> : null}
      </section>

      <section className="processes-section" aria-labelledby="decision-table">
        <h2 id="decision-table">{t("table")}</h2>
        <p className="field-hint">{t("tableHint")}</p>
        <div className="processes-table-scroll">
          <table className="processes-table">
            <caption className="visually-hidden">{t("table")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("rule")}</th>
                {draft.inputs.map((input, n) => (
                  <th scope="col" key={input.id} className="processes-input-head">
                    <FormField
                      label={t("inputLabel", { number: String(n + 1) })}
                      name={`input-label-${n}`}
                      value={input.label}
                      onChange={(e) => change((d) => ({ ...d, inputs: d.inputs.map((x, i) => (i === n ? { ...x, label: e.target.value } : x)) }))}
                    />
                    <FormField
                      label={t("inputExpression", { number: String(n + 1) })}
                      name={`input-expression-${n}`}
                      dir="ltr"
                      value={input.expression}
                      error={problemFor(input.id).map(problemText).join(" ") || null}
                      onChange={(e) => change((d) => ({ ...d, inputs: d.inputs.map((x, i) => (i === n ? { ...x, expression: e.target.value } : x)) }))}
                    />
                    {inputCount > 1 ? (
                      <IconButton
                        icon="trash"
                        label={t("removeInput", { number: String(n + 1) })}
                        onClick={() => change((d) => ({ ...d, inputs: d.inputs.filter((_, i) => i !== n), rules: d.rules.map((r) => ({ ...r, inputs: r.inputs.filter((_, i) => i !== n) })) }))}
                      />
                    ) : null}
                  </th>
                ))}
                {draft.outputs.map((output, n) => (
                  <th scope="col" key={output.id} className="processes-output-head">
                    <FormField
                      label={t("outputName", { number: String(n + 1) })}
                      name={`output-name-${n}`}
                      dir="ltr"
                      value={output.name}
                      error={problemFor(output.id).map(problemText).join(" ") || null}
                      onChange={(e) => change((d) => ({ ...d, outputs: d.outputs.map((x, i) => (i === n ? { ...x, name: e.target.value } : x)) }))}
                    />
                    {draft.hitPolicy === "PRIORITY" || draft.hitPolicy === "OUTPUT ORDER" ? (
                      <FormField
                        label={t("outputValues", { number: String(n + 1) })}
                        hint={t("outputValuesHint")}
                        name={`output-values-${n}`}
                        dir="ltr"
                        value={output.values}
                        onChange={(e) => change((d) => ({ ...d, outputs: d.outputs.map((x, i) => (i === n ? { ...x, values: e.target.value } : x)) }))}
                      />
                    ) : null}
                    {outputCount > 1 ? (
                      <IconButton
                        icon="trash"
                        label={t("removeOutput", { number: String(n + 1) })}
                        onClick={() => change((d) => ({ ...d, outputs: d.outputs.filter((_, i) => i !== n), rules: d.rules.map((r) => ({ ...r, outputs: r.outputs.filter((_, i) => i !== n) })) }))}
                      />
                    ) : null}
                  </th>
                ))}
                <th scope="col">
                  <span className="visually-hidden">{t("ruleActions")}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {draft.rules.map((rule, r) => {
                const ruleProblems = problemFor(rule.id);
                return (
                  <tr key={rule.id} aria-invalid={ruleProblems.length > 0 || undefined}>
                    <th scope="row">
                      {r + 1}
                      {ruleProblems.length > 0 ? (
                        <span className="field-error processes-rule-problem">{ruleProblems.map(problemText).join(" ")}</span>
                      ) : null}
                    </th>
                    {draft.inputs.map((_, n) => (
                      <td key={`i${n}`}>
                        <input
                          className="processes-cell"
                          dir="ltr"
                          aria-label={t("inputCell", { rule: String(r + 1), number: String(n + 1) })}
                          value={rule.inputs[n] ?? "-"}
                          onChange={(e) => change((d) => ({ ...d, rules: d.rules.map((x, i) => (i === r ? { ...x, inputs: d.inputs.map((_, k) => (k === n ? e.target.value : (x.inputs[k] ?? "-"))) } : x)) }))}
                        />
                      </td>
                    ))}
                    {draft.outputs.map((_, n) => (
                      <td key={`o${n}`}>
                        <input
                          className="processes-cell"
                          dir="ltr"
                          aria-label={t("outputCell", { rule: String(r + 1), number: String(n + 1) })}
                          value={rule.outputs[n] ?? ""}
                          onChange={(e) => change((d) => ({ ...d, rules: d.rules.map((x, i) => (i === r ? { ...x, outputs: d.outputs.map((_, k) => (k === n ? e.target.value : (x.outputs[k] ?? ""))) } : x)) }))}
                        />
                      </td>
                    ))}
                    <td>
                      <span className="processes-actions">
                        <IconButton
                          icon="arrow-up"
                          label={t("moveUp", { rule: String(r + 1) })}
                          disabled={r === 0}
                          onClick={() => change((d) => {
                            const rules = [...d.rules];
                            const [moved] = rules.splice(r, 1);
                            if (moved) rules.splice(r - 1, 0, moved);
                            return { ...d, rules };
                          })}
                        />
                        <IconButton
                          icon="trash"
                          label={t("removeRule", { rule: String(r + 1) })}
                          disabled={draft.rules.length === 1}
                          onClick={() => change((d) => ({ ...d, rules: d.rules.filter((_, i) => i !== r) }))}
                        />
                      </span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        <div className="processes-actions">
          <Button
            size="sm"
            icon="plus"
            onClick={() => change((d) => ({ ...d, rules: [...d.rules, { id: newId("rule"), inputs: d.inputs.map(() => "-"), outputs: d.outputs.map(() => ""), description: "" }] }))}
          >
            {t("addRule")}
          </Button>
          <Button
            size="sm"
            icon="plus"
            onClick={() => change((d) => ({ ...d, inputs: [...d.inputs, { id: newId("input"), label: "", expression: "" }], rules: d.rules.map((r) => ({ ...r, inputs: [...r.inputs, "-"] })) }))}
          >
            {t("addInput")}
          </Button>
          <Button
            size="sm"
            icon="plus"
            onClick={() => change((d) => ({ ...d, outputs: [...d.outputs, { id: newId("output"), name: "", values: "" }], rules: d.rules.map((r) => ({ ...r, outputs: [...r.outputs, ""] })) }))}
          >
            {t("addOutput")}
          </Button>
        </div>
        <div className="processes-actions">
          <Button variant="primary" onClick={save} busy={busy === "save"} busyLabel={t("working")} disabled={!key.trim()}>
            {t("save")}
          </Button>
          {view ? (
            <Button onClick={publish} busy={busy === "publish"} busyLabel={t("working")} disabled={problems.length > 0}>
              {t("publish")}
            </Button>
          ) : null}
        </div>
      </section>

      {view ? (
        <section className="processes-section" aria-labelledby="decision-test">
          <h2 id="decision-test">{t("test")}</h2>
          <TextAreaField
            label={t("testRows")}
            hint={t("testRowsHint")}
            name="testRows"
            dir="ltr"
            className="processes-code"
            rows={4}
            value={testRows}
            onChange={(e) => setTestRows(e.target.value)}
          />
          <Button onClick={test} busy={busy === "test"} busyLabel={t("working")} disabled={!testRows.trim()}>
            {t("runTest")}
          </Button>
          {results ? (
            <ol className="processes-list" aria-label={t("testResults")}>
              {results.map(({ row, result }, i) => (
                <li key={i}>
                  <code dir="ltr">{row}</code>{" "}
                  {result.error ? (
                    <Badge tone="danger">{t.has(`testErrors.${result.error}`) ? t(`testErrors.${result.error}`) : result.error}</Badge>
                  ) : (
                    <>
                      <code dir="ltr">{result.result === null ? t("noResult") : JSON.stringify(result.result)}</code>{" "}
                      <span className="field-hint">{result.rules.length > 0 ? t("matchedRules", { rules: result.rules.join(", ") }) : t("noRule")}</span>
                    </>
                  )}
                </li>
              ))}
            </ol>
          ) : null}
        </section>
      ) : null}

      {view && view.versions.length > 0 ? (
        <section className="processes-section" aria-labelledby="decision-versions">
          <h2 id="decision-versions">{t("versions")}</h2>
          <ul className="processes-list">
            {view.versions.map((v) => (
              <li key={v.version}>
                {t("versionLine", { version: v.version, when: format.dateTime(new Date(v.publishedAt), { dateStyle: "medium", timeStyle: "short" }) })}
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}
