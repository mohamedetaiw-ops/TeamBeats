"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { Switch } from "@/components/ui/Switch";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import type { DetectionPattern, DetectionSettings } from "@/lib/security/types";

const RETURN_TO = "/admin/security/detection";
const TARGETS = ["guest_space", "everyone", "other"] as const;
const MODES = ["off", "warn", "block"] as const;

/**
 * Sensitive data detection (spec 026 US6, FR-015): turn it on, choose off, warn or block for each kind of location, see
 * the built-in types, and keep the organization's own patterns. A pattern is tried on sample text and is turned on
 * only once the sample matched; the API enforces that and every permission. Samples are not saved.
 */
export function DetectionAdmin({ initial }: { initial: DetectionSettings }) {
  const t = useTranslations("security.detection");
  const action = useAdminAction(RETURN_TO);
  const [view, setView] = useState(initial);
  const [enabled, setEnabled] = useState(initial.enabled);
  const [modes, setModes] = useState(initial.modes);
  const [name, setName] = useState("");
  const [pattern, setPattern] = useState("");
  const [sample, setSample] = useState("");
  const [turnOn, setTurnOn] = useState(true);
  const [trial, setTrial] = useState<string | null>(null);

  async function reload() {
    const response = await fetch("/api/v1/security/detection", { cache: "no-store" }).catch(() => null);
    if (response?.ok) setView((await response.json()) as DetectionSettings);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    const outcome = await action.run<DetectionSettings>("save", "PUT", "/api/v1/security/detection", { enabled, modes }, t("saved"));
    if (outcome.ok) setView(outcome.data);
  }

  async function test() {
    setTrial(null);
    const outcome = await action.run<{ valid: boolean; matches: number }>("test", "POST", "/api/v1/security/detection/patterns:test", { pattern, sample }, (r) =>
      r.valid ? t("testResult", { count: r.matches }) : t("testInvalid"),
    );
    if (outcome.ok) {
      action.setResult(null);
      setTrial(outcome.data.valid ? t("testResult", { count: outcome.data.matches }) : t("testInvalid"));
    }
  }

  async function add(e: FormEvent) {
    e.preventDefault();
    const outcome = await action.run("add", "POST", "/api/v1/security/detection/patterns", { name, pattern, sample, enabled: turnOn }, t("added"));
    if (outcome.ok) {
      setName("");
      setPattern("");
      setSample("");
      setTrial(null);
      await reload();
    }
  }

  async function toggle(p: DetectionPattern) {
    const outcome = await action.run("toggle:" + p.id, "PUT", `/api/v1/security/detection/patterns/${p.id}`, { enabled: !p.enabled }, t(p.enabled ? "turnedOff" : "turnedOn"));
    if (outcome.ok) await reload();
  }

  async function remove(p: DetectionPattern) {
    const outcome = await action.run("delete:" + p.id, "DELETE", `/api/v1/security/detection/patterns/${p.id}`, undefined, t("deleted"));
    if (outcome.ok) await reload();
  }

  return (
    <div className="security">
      <ResultAlert result={action.result} />
      <form onSubmit={(e) => void save(e)} aria-labelledby="detection-settings">
        <h2 id="detection-settings">{t("settingsTitle")}</h2>
        <Switch label={t("enabled")} hint={t("enabledHint")} checked={enabled} disabled={!view.canManage} onChange={(e) => setEnabled(e.target.checked)} />
        <div className="admin-grid">
          {TARGETS.map((target) => (
            <SelectField
              key={target}
              label={t(`targets.${target}`)}
              hint={t(`targetHints.${target}`)}
              name={`mode-${target}`}
              value={modes[target]}
              disabled={!view.canManage}
              onChange={(e) => setModes({ ...modes, [target]: e.target.value as (typeof MODES)[number] })}
              options={MODES.map((m) => ({ value: m, label: t(`modes.${m}`) }))}
            />
          ))}
        </div>
        {view.canManage ? (
          <div className="ui-control-row">
            <Button type="submit" variant="primary" busy={action.busy === "save"} busyLabel={t("working")}>
              {t("save")}
            </Button>
          </div>
        ) : (
          <p className="notice">{t("readOnly")}</p>
        )}
      </form>

      <section aria-labelledby="detection-builtin">
        <h2 id="detection-builtin">{t("builtInTitle")}</h2>
        <p>{t("builtInIntro")}</p>
        <ul className="ui-control-row">
          {view.builtIn.map((b) => (
            <li key={b}>
              <Badge>{t(`types.${b}`)}</Badge>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="detection-patterns">
        <h2 id="detection-patterns">{t("patternsTitle")}</h2>
        <DataTable<DetectionPattern>
          caption={t("patternsTitle")}
          rows={view.patterns}
          rowKey={(p) => p.id}
          emptyTitle={t("none")}
          emptyMessage={t("noneHint")}
          columns={[
            { key: "name", header: t("name"), rowHeader: true, cell: (p) => p.name },
            { key: "pattern", header: t("pattern"), cell: (p) => <bdi dir="ltr"><code>{p.pattern}</code></bdi> },
            { key: "state", header: t("state"), cell: (p) => <Badge tone={p.enabled ? "success" : "neutral"}>{t(p.enabled ? "on" : "off")}</Badge> },
            {
              key: "actions",
              header: t("actions"),
              align: "end",
              cell: (p) =>
                view.canEditPatterns ? (
                  <div className="ui-control-row">
                    <Button busy={action.busy === "toggle:" + p.id} busyLabel={t("working")} onClick={() => void toggle(p)}>
                      {t(p.enabled ? "turnOff" : "turnOn")}
                    </Button>
                    <ConfirmAction
                      label={t("delete")}
                      question={t("deleteQuestion", { name: p.name })}
                      confirmLabel={t("delete")}
                      cancelLabel={t("cancel")}
                      onConfirm={() => remove(p)}
                    />
                  </div>
                ) : null,
            },
          ]}
        />
        {view.canEditPatterns ? (
          <form onSubmit={(e) => void add(e)} aria-labelledby="detection-add">
            <h3 id="detection-add">{t("addTitle")}</h3>
            <div className="admin-grid">
              <FormField label={t("name")} name="name" required maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
              <FormField label={t("pattern")} hint={t("patternHint")} name="pattern" dir="ltr" required maxLength={500} value={pattern} onChange={(e) => setPattern(e.target.value)} />
            </div>
            <TextAreaField label={t("sample")} hint={t("sampleHint")} name="sample" maxLength={10000} value={sample} onChange={(e) => setSample(e.target.value)} />
            {trial ? (
              <p role="status" className="notice">
                {trial}
              </p>
            ) : null}
            <Checkbox label={t("turnOnNow")} checked={turnOn} onChange={(e) => setTurnOn(e.target.checked)} />
            <div className="ui-control-row">
              <Button busy={action.busy === "test"} busyLabel={t("working")} onClick={() => void test()}>
                {t("test")}
              </Button>
              <Button type="submit" variant="primary" busy={action.busy === "add"} busyLabel={t("working")}>
                {t("add")}
              </Button>
            </div>
          </form>
        ) : null}
      </section>
    </div>
  );
}
