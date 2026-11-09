"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { pickText } from "@/lib/agents/types";
import { send } from "@/lib/evaluation/send";
import { SEVERITIES, type AgentSuites, type DatasetView, type MetricDefinition, type SuiteMetric } from "@/lib/evaluation/types";

type Row = { key: string; weight: string; threshold: string; severity: string };

function toRow(m: SuiteMetric): Row {
  return { key: m.key, weight: String(m.weight), threshold: m.threshold === null ? "" : String(Math.round(m.threshold * 10000) / 100), severity: m.severity ?? "" };
}

/**
 * An agent's suites (spec 034 US1): the inherited platform and safety suites, read only; the rule that applies to each
 * metric with the level it came from; and the agent's own suite, whose metrics, thresholds, severities, weights and
 * datasets the designer edits. Thresholds are entered as percentages. The API checks every rule and names the level a
 * change would weaken.
 */
export function SuiteEditor({ initial, catalog }: { initial: AgentSuites; catalog: MetricDefinition[] }) {
  const t = useTranslations("evaluation");
  const locale = useLocale();
  const router = useRouter();
  const ids = useId();
  const [view, setView] = useState(initial);
  const [rows, setRows] = useState<Row[]>(initial.suite.metrics.map(toRow));
  const [datasets, setDatasets] = useState<string[]>(initial.suite.datasets);
  const [names, setNames] = useState({ en: "", ar: "" });
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const level = (l: string | null) => (l ? t(`level.${l}`) : t("none"));
  const percent = (v: number | null) => (v === null ? t("none") : `${Math.round(v * 10000) / 100}%`);
  const unused = catalog.filter((m) => !rows.some((r) => r.key === m.key));

  function update(i: number, patch: Partial<Row>) {
    setRows((current) => current.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  }

  async function save() {
    setBusy("save");
    setResult(null);
    const metrics = rows.map((r) => ({
      key: r.key,
      weight: r.weight.trim() === "" ? 1 : Number(r.weight),
      threshold: r.threshold.trim() === "" ? null : Number(r.threshold) / 100,
      severity: r.severity === "" ? null : r.severity,
    }));
    const sent = await send<AgentSuites>(`/api/v1/evaluation/agents/${view.agentId}/suite`, "PUT", { version: view.suite.version, metrics, datasets }, t("failed"));
    setBusy(null);
    if (sent.ok) {
      setView(sent.data);
      setRows(sent.data.suite.metrics.map(toRow));
      setDatasets(sent.data.suite.datasets);
      setResult({ ok: true, messages: [t("suite.saved", { version: String(sent.data.suite.version) })] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  async function createDataset() {
    setBusy("dataset");
    setResult(null);
    const given = Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim() !== ""));
    const sent = await send<DatasetView>("/api/v1/evaluation/datasets", "POST", { agentId: view.agentId, names: given }, t("failed"));
    setBusy(null);
    if (sent.ok) router.push(`/admin/evaluation/datasets/${sent.data.id}`);
    else setResult({ ok: false, messages: sent.messages });
  }

  return (
    <div className="agents">
      <section className="agents-section" aria-labelledby={`${ids}-inherited`}>
        <h2 id={`${ids}-inherited`}>{t("suite.inherited")}</h2>
        <p>{t("suite.inheritedHint")}</p>
        <div className="agents-table">
          <table data-testid="eval-inherited">
            <thead>
              <tr>
                <th scope="col">{t("run.suite")}</th>
                <th scope="col">{t("suite.kind")}</th>
                <th scope="col">{t("dashboard.version")}</th>
                <th scope="col">{t("run.cases")}</th>
                <th scope="col">{t("suite.metricsUsed")}</th>
              </tr>
            </thead>
            <tbody>
              {view.inherited.map((s) => (
                <tr key={s.key}>
                  <th scope="row">
                    <bdi>{pickText(s.names, locale) || s.key}</bdi>
                  </th>
                  <td>{s.mandatory ? t("suite.mandatory") : t("suite.template")}</td>
                  <td data-number>{s.version}</td>
                  <td data-number>{s.caseCount}</td>
                  <td>{s.metrics.map((m) => label(m.key)).join(t("listSeparator"))}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-rules`}>
        <h2 id={`${ids}-rules`}>{t("suite.rules")}</h2>
        <p>{t("suite.rulesHint")}</p>
        <div className="agents-table">
          <table data-testid="eval-rules">
            <thead>
              <tr>
                <th scope="col">{t("metric")}</th>
                <th scope="col">{t("run.threshold")}</th>
                <th scope="col">{t("suite.thresholdFrom")}</th>
                <th scope="col">{t("run.severity")}</th>
                <th scope="col">{t("suite.severityFrom")}</th>
              </tr>
            </thead>
            <tbody>
              {view.rules.map((r) => (
                <tr key={r.key}>
                  <th scope="row">{label(r.key)}</th>
                  <td data-number>{percent(r.threshold)}</td>
                  <td>{level(r.thresholdLevel)}</td>
                  <td>{r.severity ? t(`severity.${r.severity}`) : t("none")}</td>
                  <td>{level(r.severityLevel)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-own`}>
        <h2 id={`${ids}-own`}>{t("suite.own", { version: String(view.suite.version) })}</h2>
        {rows.length === 0 ? <p>{t("suite.noMetrics")}</p> : null}
        {rows.length > 0 ? (
          <div className="agents-table">
            <table data-testid="eval-own-metrics">
              <thead>
                <tr>
                  <th scope="col">{t("metric")}</th>
                  {view.weightsAllowed ? <th scope="col">{t("suite.weight")}</th> : null}
                  <th scope="col">{t("suite.thresholdPercent")}</th>
                  <th scope="col">{t("run.severity")}</th>
                  {view.canEdit ? <th scope="col">{t("suite.actions")}</th> : null}
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.key}>
                    <th scope="row">{label(r.key)}</th>
                    {view.weightsAllowed ? (
                      <td>
                        <input
                          type="number"
                          min={0}
                          step="0.1"
                          aria-label={t("suite.weightFor", { metric: label(r.key) })}
                          value={r.weight}
                          disabled={!view.canEdit}
                          onChange={(e) => update(i, { weight: e.target.value })}
                        />
                      </td>
                    ) : null}
                    <td>
                      <input
                        type="number"
                        min={0}
                        max={100}
                        step="0.5"
                        aria-label={t("suite.thresholdFor", { metric: label(r.key) })}
                        placeholder={t("suite.inherit")}
                        value={r.threshold}
                        disabled={!view.canEdit}
                        onChange={(e) => update(i, { threshold: e.target.value })}
                      />
                    </td>
                    <td>
                      <select
                        aria-label={t("suite.severityFor", { metric: label(r.key) })}
                        value={r.severity}
                        disabled={!view.canEdit}
                        onChange={(e) => update(i, { severity: e.target.value })}
                      >
                        <option value="">{t("suite.inherit")}</option>
                        {SEVERITIES.map((s) => (
                          <option key={s} value={s}>
                            {t(`severity.${s}`)}
                          </option>
                        ))}
                      </select>
                    </td>
                    {view.canEdit ? (
                      <td>
                        <Button variant="quiet" onClick={() => setRows((current) => current.filter((_, j) => j !== i))}>
                          {t("suite.remove", { metric: label(r.key) })}
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
          <div className="agents-row">
            <div className="field">
              <label htmlFor={`${ids}-add`}>{t("suite.addMetric")}</label>
              <select
                id={`${ids}-add`}
                value=""
                onChange={(e) => e.target.value && setRows((current) => [...current, { key: e.target.value, weight: "1", threshold: "", severity: "" }])}
              >
                <option value="">{t("suite.choose")}</option>
                {unused.map((m) => (
                  <option key={m.key} value={m.key}>
                    {label(m.key)}
                  </option>
                ))}
              </select>
            </div>
          </div>
        ) : null}
        <fieldset className="agents-checks">
          <legend>{t("suite.datasets")}</legend>
          {view.datasets.length === 0 ? <p>{t("suite.noDatasets")}</p> : null}
          {view.datasets.map((d) => (
            <label key={d.id}>
              <input
                type="checkbox"
                checked={datasets.includes(d.id)}
                disabled={!view.canEdit}
                onChange={(e) => setDatasets((current) => (e.target.checked ? [...current, d.id] : current.filter((x) => x !== d.id)))}
              />
              <bdi>{pickText(d.names, locale) || t("untitled")}</bdi>
            </label>
          ))}
        </fieldset>
        {view.canEdit ? (
          <div className="agents-row">
            <Button variant="primary" busy={busy === "save"} busyLabel={t("working")} onClick={() => void save()} data-testid="eval-suite-save">
              {t("suite.save")}
            </Button>
          </div>
        ) : null}
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-datasets`}>
        <h2 id={`${ids}-datasets`}>{t("suite.datasetList")}</h2>
        {view.datasets.length === 0 ? (
          <p>{t("suite.noDatasets")}</p>
        ) : (
          <div className="agents-table">
            <table data-testid="eval-datasets">
              <thead>
                <tr>
                  <th scope="col">{t("dataset.name")}</th>
                  <th scope="col">{t("dashboard.version")}</th>
                  <th scope="col">{t("run.cases")}</th>
                  <th scope="col">{t("dataset.locales")}</th>
                  <th scope="col">{t("dataset.classification")}</th>
                </tr>
              </thead>
              <tbody>
                {view.datasets.map((d) => (
                  <tr key={d.id}>
                    <th scope="row">
                      <Link href={`/admin/evaluation/datasets/${d.id}`}>
                        <bdi>{pickText(d.names, locale) || t("untitled")}</bdi>
                      </Link>
                    </th>
                    <td data-number>{d.version}</td>
                    <td data-number>{d.caseCount}</td>
                    <td>{d.locales.map((l) => t(`locales.${l}`)).join(t("listSeparator")) || t("none")}</td>
                    <td>{t(`classification.${d.classification}`)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {view.canEdit ? (
          <div className="agents-row">
            {(["en", "ar"] as const).map((l) => (
              <div className="field" key={l}>
                <label htmlFor={`${ids}-name-${l}`}>{t("dataset.nameIn", { locale: t(`locales.${l}`) })}</label>
                <input
                  id={`${ids}-name-${l}`}
                  type="text"
                  dir={l === "ar" ? "rtl" : "ltr"}
                  value={names[l]}
                  onChange={(e) => setNames((current) => ({ ...current, [l]: e.target.value }))}
                />
              </div>
            ))}
            <Button busy={busy === "dataset"} busyLabel={t("working")} onClick={() => void createDataset()} data-testid="eval-dataset-create">
              {t("dataset.create")}
            </Button>
          </div>
        ) : null}
      </section>
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
