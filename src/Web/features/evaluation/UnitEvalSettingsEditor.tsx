"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";
import { pickText } from "@/lib/agents/types";
import { SEVERITIES, type DatasetSummary, type DatasetView, type MetricDefinition, type SuiteMetric, type UnitSettingsView } from "@/lib/evaluation/types";

type Row = { key: string; threshold: string; severity: string };

const toRow = (m: SuiteMetric): Row => ({ key: m.key, threshold: m.threshold === null ? "" : String(Math.round(m.threshold * 10000) / 100), severity: m.severity ?? "" });

/**
 * The evaluation settings of one organization unit (spec 034 US10, FR-020): what the levels above it set, the rules in
 * force for its agents, and its own thresholds, which may only be stricter, with the unit's datasets that its agents'
 * runs add. The API refuses a looser rule and names the level it would loosen.
 */
export function UnitEvalSettingsEditor({ initial, catalog, locale }: { initial: UnitSettingsView; catalog: MetricDefinition[]; locale: string }) {
  const t = useTranslations("evaluation.units");
  const te = useTranslations("evaluation");
  const ids = useId();
  const [view, setView] = useState(initial);
  const [rows, setRows] = useState<Row[]>(initial.metrics.map(toRow));
  const [chosen, setChosen] = useState<string[]>(initial.datasets);
  const [own, setOwn] = useState<DatasetSummary[]>(initial.ownDatasets);
  const [names, setNames] = useState({ en: "", ar: "" });
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const label = (key: string) => (te.has(`metrics.${key}`) ? te(`metrics.${key}`) : key);
  const percent = (v: number | null) => (v === null ? te("none") : `${Math.round(v * 10000) / 100}%`);
  const level = (l: string | null) => (l === null ? te("none") : te.has(`level.${l}`) ? te(`level.${l}`) : l);
  const unused = catalog.filter((m) => !rows.some((r) => r.key === m.key));

  function update(i: number, patch: Partial<Row>) {
    setRows((current) => current.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  }

  async function save() {
    setBusy("save");
    setResult(null);
    const metrics = rows.map((r) => ({
      key: r.key,
      weight: 1,
      threshold: r.threshold.trim() === "" ? null : Number(r.threshold) / 100,
      severity: r.severity === "" ? null : r.severity,
    }));
    const sent = await send<UnitSettingsView>(`/api/v1/evaluation/units/${view.unitId}/settings`, "PUT", { version: view.version, metrics, datasets: chosen }, te("failed"));
    setBusy(null);
    if (sent.ok) {
      setView(sent.data);
      setRows(sent.data.metrics.map(toRow));
      setChosen(sent.data.datasets);
      setOwn(sent.data.ownDatasets);
      setResult({ ok: true, messages: [t("saved")] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  async function create() {
    setBusy("create");
    setResult(null);
    const given = Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim() !== "").map(([k, v]) => [k, v.trim()]));
    const sent = await send<DatasetView>("/api/v1/evaluation/datasets", "POST", { agentId: null, orgUnitId: view.unitId, names: given }, te("failed"));
    setBusy(null);
    if (sent.ok) {
      const d = sent.data;
      setOwn((current) => [...current, { id: d.id, names: d.names, version: d.version, caseCount: 0, classification: "internal", locales: [], updatedAt: new Date().toISOString() }]);
      setNames({ en: "", ar: "" });
      setResult({ ok: true, messages: [t("created")] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  return (
    <div className="agents">
      <section className="agents-section" aria-labelledby={`${ids}-above`}>
        <h2 id={`${ids}-above`}>{t("above")}</h2>
        {view.above.length === 0 ? <p>{t("noAbove")}</p> : null}
        {view.above.length > 0 ? (
          <ul>
            {view.above.map((a) => (
              <li key={a.unitId}>
                <bdi>{pickText(a.unitNames, locale) || te("untitled")}</bdi> {t("aboveValue", { rules: String(a.metrics.length), datasets: String(a.datasetCount) })}
              </li>
            ))}
          </ul>
        ) : null}
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-rules`}>
        <h2 id={`${ids}-rules`}>{t("rules")}</h2>
        {view.rules.length === 0 ? <p>{te("settings.noRules")}</p> : null}
        {view.rules.length > 0 ? (
          <div className="agents-table">
            <table data-testid="eval-unit-rules">
              <thead>
                <tr>
                  <th scope="col">{te("metric")}</th>
                  <th scope="col">{te("run.threshold")}</th>
                  <th scope="col">{t("thresholdFrom")}</th>
                  <th scope="col">{te("run.severity")}</th>
                  <th scope="col">{t("severityFrom")}</th>
                </tr>
              </thead>
              <tbody>
                {view.rules.map((r) => (
                  <tr key={r.key}>
                    <th scope="row">{label(r.key)}</th>
                    <td data-number>{percent(r.threshold)}</td>
                    <td>{level(r.thresholdLevel)}</td>
                    <td>{r.severity ? te(`severity.${r.severity}`) : te("none")}</td>
                    <td>{level(r.severityLevel)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-own`}>
        <h2 id={`${ids}-own`}>{t("own")}</h2>
        <p>{t("ownHint")}</p>
        {rows.length === 0 ? <p>{te("settings.noRules")}</p> : null}
        {rows.length > 0 ? (
          <div className="agents-table">
            <table data-testid="eval-unit-own">
              <thead>
                <tr>
                  <th scope="col">{te("metric")}</th>
                  <th scope="col">{te("suite.thresholdPercent")}</th>
                  <th scope="col">{te("run.severity")}</th>
                  {view.canEdit ? <th scope="col">{te("suite.actions")}</th> : null}
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.key}>
                    <th scope="row">{label(r.key)}</th>
                    <td>
                      <input
                        type="number"
                        min={0}
                        max={100}
                        step="0.5"
                        aria-label={te("suite.thresholdFor", { metric: label(r.key) })}
                        placeholder={te("suite.inherit")}
                        value={r.threshold}
                        disabled={!view.canEdit}
                        onChange={(e) => update(i, { threshold: e.target.value })}
                      />
                    </td>
                    <td>
                      <select aria-label={te("suite.severityFor", { metric: label(r.key) })} value={r.severity} disabled={!view.canEdit} onChange={(e) => update(i, { severity: e.target.value })}>
                        <option value="">{te("suite.inherit")}</option>
                        {SEVERITIES.map((s) => (
                          <option key={s} value={s}>
                            {te(`severity.${s}`)}
                          </option>
                        ))}
                      </select>
                    </td>
                    {view.canEdit ? (
                      <td>
                        <Button variant="quiet" onClick={() => setRows((current) => current.filter((_, j) => j !== i))}>
                          {te("suite.remove", { metric: label(r.key) })}
                        </Button>
                      </td>
                    ) : null}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        {view.canEdit && unused.length > 0 ? (
          <div className="field">
            <label htmlFor={`${ids}-add`}>{te("suite.addMetric")}</label>
            <select id={`${ids}-add`} value="" onChange={(e) => e.target.value && setRows((current) => [...current, { key: e.target.value, threshold: "", severity: "" }])}>
              <option value="">{te("suite.choose")}</option>
              {unused.map((m) => (
                <option key={m.key} value={m.key}>
                  {label(m.key)}
                </option>
              ))}
            </select>
          </div>
        ) : null}
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-datasets`}>
        <h2 id={`${ids}-datasets`}>{t("datasets")}</h2>
        <p>{t("datasetsHint")}</p>
        {own.length === 0 ? <p>{t("noDatasets")}</p> : null}
        {own.length > 0 ? (
          <fieldset className="agents-checks">
            <legend>{t("datasetsUsed")}</legend>
            {own.map((d) => (
              <span key={d.id}>
                <label>
                  <input
                    type="checkbox"
                    checked={chosen.includes(d.id)}
                    disabled={!view.canEdit}
                    onChange={(e) => setChosen((current) => (e.target.checked ? [...current, d.id] : current.filter((x) => x !== d.id)))}
                  />
                  <bdi>{pickText(d.names, locale) || te("untitled")}</bdi>
                </label>{" "}
                <Link href={`/admin/evaluation/datasets/${d.id}`}>{t("openDataset")}</Link>
              </span>
            ))}
          </fieldset>
        ) : null}
        {view.canEdit ? (
          <>
            <div className="agents-row">
              <div className="field">
                <label htmlFor={`${ids}-name-en`}>{t("nameEn")}</label>
                <input id={`${ids}-name-en`} type="text" dir="ltr" lang="en" maxLength={120} value={names.en} onChange={(e) => setNames((n) => ({ ...n, en: e.target.value }))} />
              </div>
              <div className="field">
                <label htmlFor={`${ids}-name-ar`}>{t("nameAr")}</label>
                <input id={`${ids}-name-ar`} type="text" dir="rtl" lang="ar" maxLength={120} value={names.ar} onChange={(e) => setNames((n) => ({ ...n, ar: e.target.value }))} />
              </div>
            </div>
            <div className="agents-row">
              <Button busy={busy === "create"} busyLabel={te("working")} onClick={() => void create()} data-testid="eval-unit-dataset-create">
                {t("createDataset")}
              </Button>
            </div>
          </>
        ) : null}
      </section>
      {view.canEdit ? (
        <div className="agents-row">
          <Button variant="primary" busy={busy === "save"} busyLabel={te("working")} onClick={() => void save()} data-testid="eval-unit-save">
            {t("save")}
          </Button>
        </div>
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
