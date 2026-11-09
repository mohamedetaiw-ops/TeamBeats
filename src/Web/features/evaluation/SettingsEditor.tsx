"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";
import { SEVERITIES, type MetricDefinition, type SettingsView, type SuiteMetric } from "@/lib/evaluation/types";

type Row = { key: string; threshold: string; severity: string };

const toRow = (m: SuiteMetric): Row => ({ key: m.key, threshold: m.threshold === null ? "" : String(Math.round(m.threshold * 10000) / 100), severity: m.severity ?? "" });

/**
 * Tenant evaluation settings (spec 034 US10): the platform's thresholds and severities, read only, and the tenant's own,
 * which may only be stricter. Thresholds are entered as percentages; the API refuses a looser rule and names the level.
 */
export function SettingsEditor({ initial, catalog }: { initial: SettingsView; catalog: MetricDefinition[] }) {
  const t = useTranslations("evaluation");
  const ids = useId();
  const [view, setView] = useState(initial);
  const [rows, setRows] = useState<Row[]>(initial.thresholds.map(toRow));
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const label = (key: string) => (t.has(`metrics.${key}`) ? t(`metrics.${key}`) : key);
  const percent = (v: number | null) => (v === null ? t("none") : `${Math.round(v * 10000) / 100}%`);
  const unused = catalog.filter((m) => !rows.some((r) => r.key === m.key));

  function update(i: number, patch: Partial<Row>) {
    setRows((current) => current.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  }

  async function save() {
    setBusy(true);
    setResult(null);
    const thresholds = rows.map((r) => ({
      key: r.key,
      weight: 1,
      threshold: r.threshold.trim() === "" ? null : Number(r.threshold) / 100,
      severity: r.severity === "" ? null : r.severity,
    }));
    const sent = await send<SettingsView>("/api/v1/evaluation/settings", "PUT", { version: view.version, thresholds }, t("failed"));
    setBusy(false);
    if (sent.ok) {
      setView(sent.data);
      setRows(sent.data.thresholds.map(toRow));
      setResult({ ok: true, messages: [t("settings.saved")] });
    } else {
      setResult({ ok: false, messages: sent.messages });
    }
  }

  return (
    <div className="agents">
      <section className="agents-section" aria-labelledby={`${ids}-platform`}>
        <h2 id={`${ids}-platform`}>{t("settings.platform")}</h2>
        <div className="agents-table">
          <table data-testid="eval-platform-rules">
            <thead>
              <tr>
                <th scope="col">{t("metric")}</th>
                <th scope="col">{t("run.threshold")}</th>
                <th scope="col">{t("run.severity")}</th>
              </tr>
            </thead>
            <tbody>
              {view.platform.map((r) => (
                <tr key={r.key}>
                  <th scope="row">{label(r.key)}</th>
                  <td data-number>{percent(r.threshold)}</td>
                  <td>{r.severity ? t(`severity.${r.severity}`) : t("none")}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      <section className="agents-section" aria-labelledby={`${ids}-tenant`}>
        <h2 id={`${ids}-tenant`}>{t("settings.tenant")}</h2>
        <p>{t("settings.tenantHint")}</p>
        {rows.length === 0 ? <p>{t("settings.noRules")}</p> : null}
        {rows.length > 0 ? (
          <div className="agents-table">
            <table data-testid="eval-tenant-rules">
              <thead>
                <tr>
                  <th scope="col">{t("metric")}</th>
                  <th scope="col">{t("suite.thresholdPercent")}</th>
                  <th scope="col">{t("run.severity")}</th>
                  {view.canEdit ? <th scope="col">{t("suite.actions")}</th> : null}
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
          <div className="field">
            <label htmlFor={`${ids}-add`}>{t("suite.addMetric")}</label>
            <select id={`${ids}-add`} value="" onChange={(e) => e.target.value && setRows((current) => [...current, { key: e.target.value, threshold: "", severity: "" }])}>
              <option value="">{t("suite.choose")}</option>
              {unused.map((m) => (
                <option key={m.key} value={m.key}>
                  {label(m.key)}
                </option>
              ))}
            </select>
          </div>
        ) : null}
        {view.canEdit ? (
          <div className="agents-row">
            <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void save()} data-testid="eval-settings-save">
              {t("settings.save")}
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
