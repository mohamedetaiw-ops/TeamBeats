"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { StatCards } from "@/components/ui/Blocks";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { IconButton } from "@/components/ui/IconButton";
import { NumberField } from "@/components/ui/Inputs";
import { RadioGroup } from "@/components/ui/RadioGroup";
import {
  MAPPING_LEVELS,
  MAPPING_STRATEGIES,
  unitLabel,
  type DirectoryMappingState,
  type MappingDiff,
  type MappingRun,
  type MappingRunResult,
  type OrgMappingView,
  type OrgUnitView,
} from "@/lib/admin/types";
import { ResultAlert, useAdminAction, useKeyLabel, usePeopleNames, useWhen } from "./shared";

const RETURN = "/admin/organization/directory";
const COUNTS = ["create", "rename", "move", "deactivate", "flagged", "membershipsAdded", "membershipsEnded", "unmapped"] as const;

const lines = (text: string) =>
  text
    .split(/[\n,]/u)
    .map((s) => s.trim())
    .filter(Boolean);

function countsOf(diff: MappingDiff): Record<(typeof COUNTS)[number], number> {
  return {
    create: diff.create.length + diff.reactivate.length,
    rename: diff.rename.length,
    move: diff.move.length,
    deactivate: diff.deactivate.length,
    flagged: diff.flagged.length,
    membershipsAdded: diff.addMemberships.length,
    membershipsEnded: diff.endMemberships.length,
    unmapped: diff.unmapped.length,
  };
}

/**
 * Organization from the directory (spec 002 US5): how units are derived from the directory, alias rules, values mapped
 * to units managed in TeamBeats, a preview that changes nothing, and the approval after which every directory sync
 * applies the mapping. Changing the mapping is a whole-workspace action; others see the mapping and its runs only. The
 * API decides both, and the deletion threshold stops a run that would remove too much.
 */
export function DirectoryMappingAdmin({
  initial,
  units,
  wholeTenant,
}: {
  initial: DirectoryMappingState;
  units: OrgUnitView[];
  wholeTenant: boolean;
}) {
  const t = useTranslations("admin.org.directory");
  const locale = useLocale();
  const when = useWhen();
  const strategyName = useKeyLabel("admin.org.directory.strategies");
  const { busy, result, run } = useAdminAction(RETURN);
  const [mapping, setMapping] = useState<OrgMappingView | null>(initial.mapping);
  const [runs, setRuns] = useState<MappingRun[]>(initial.runs);
  const [preview, setPreview] = useState<MappingRunResult | null>(null);

  const current = mapping?.settings;
  const [strategy, setStrategy] = useState(mapping?.strategy ?? "attributes");
  const [levels, setLevels] = useState<string[]>(current?.levels.length ? current.levels : ["company", "department"]);
  const [groups, setGroups] = useState((current?.groups ?? []).join("\n"));
  const [aliases, setAliases] = useState(current?.aliases ?? []);
  const [valueMap, setValueMap] = useState(current?.valueMap ?? []);
  const [parent, setParent] = useState(current?.parentUnitId ?? "");
  const [threshold, setThreshold] = useState(mapping?.deletionThresholdPct ?? 10);

  const byId = useMemo(() => new Map(units.map((u) => [u.id, u])), [units]);
  const unitName = (id: string | null | undefined) => (id ? (byId.get(id) ? unitLabel(byId.get(id)!, locale) : id.slice(0, 8)) : t("topLevel"));
  const productUnits = units.filter((u) => u.owner === "product" && u.status === "active");
  const activeUnits = units.filter((u) => u.status === "active");
  const person = usePeopleNames(preview ? preview.diff.unmapped.map((u) => u.userId) : []);

  async function save() {
    const body = {
      strategy,
      levels: MAPPING_LEVELS.filter((l) => levels.includes(l)),
      groups: lines(groups),
      aliases: aliases.filter((a) => a.from.trim() || a.to.trim()),
      valueMap: valueMap.filter((v) => v.key.trim() || v.unitId),
      parentUnitId: parent || null,
      deletionThresholdPct: threshold,
    };
    const outcome = await run<OrgMappingView>("save", "PUT", "/api/v1/org/directory-mapping", body, t("saved"));
    if (outcome.ok) {
      setMapping(outcome.data);
      setPreview(null);
    }
  }

  async function runPreview() {
    const outcome = await run<MappingRunResult>("preview", "POST", "/api/v1/org/directory-mapping:preview", null, t("previewed"));
    if (outcome.ok) {
      setPreview(outcome.data);
      setRuns((before) => [outcome.data.run, ...before].slice(0, 10));
      setMapping((m) => (m ? { ...m, previewedAt: outcome.data.run.startedAt } : m));
    }
  }

  async function approve() {
    const outcome = await run<MappingRunResult>("approve", "POST", "/api/v1/org/directory-mapping:approve", null, (r) =>
      r.run.outcome === "succeeded" ? t("applied") : t("stopped"),
    );
    if (outcome.ok) {
      setPreview(outcome.data);
      setRuns((before) => [outcome.data.run, ...before].slice(0, 10));
      setMapping((m) => (m ? { ...m, approvedAt: outcome.data.run.startedAt } : m));
    }
  }

  const status = !mapping
    ? { tone: "neutral" as const, text: t("status.notSet") }
    : mapping.approvedAt
      ? { tone: "success" as const, text: t("status.approved", { when: when(mapping.approvedAt, "short") }) }
      : mapping.previewedAt
        ? { tone: "warning" as const, text: t("status.previewed") }
        : { tone: "warning" as const, text: t("status.saved") };

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />

      <section className="admin-panel" aria-labelledby="mapping-title">
        <div className="admin-panel-head">
          <h2 id="mapping-title">{t("title")}</h2>
          <Badge tone={status.tone}>{status.text}</Badge>
        </div>
        <p className="admin-meta">{t("intro")}</p>
        {!wholeTenant ? <Alert tone="info" title={t("readOnly")} /> : null}

        <RadioGroup
          legend={t("strategy")}
          name="strategy"
          value={strategy}
          onChange={(e) => setStrategy(e.target.value)}
          options={MAPPING_STRATEGIES.map((s) => ({
            value: s,
            label: strategyName(s),
            hint: t(`strategyHints.${s}`),
            disabled: !wholeTenant || s === "ou_path",
          }))}
        />

        {strategy === "attributes" ? (
          <fieldset className="admin-fieldset">
            <legend>{t("levels")}</legend>
            <p className="admin-meta">{t("levelsHint")}</p>
            <div className="admin-choices">
              {MAPPING_LEVELS.map((l) => (
                <Checkbox
                  key={l}
                  name={`level-${l}`}
                  label={t(`levelNames.${l}`)}
                  checked={levels.includes(l)}
                  disabled={!wholeTenant}
                  onChange={(e) => setLevels(e.target.checked ? [...levels, l] : levels.filter((x) => x !== l))}
                />
              ))}
            </div>
          </fieldset>
        ) : null}

        {strategy === "groups" ? (
          <TextAreaField
            label={t("groups")}
            hint={t("groupsHint")}
            name="groups"
            dir="ltr"
            rows={4}
            value={groups}
            disabled={!wholeTenant}
            onChange={(e) => setGroups(e.target.value)}
          />
        ) : null}

        <div className="admin-fields">
          <SelectField
            label={t("parent")}
            hint={t("parentHint")}
            name="parentUnitId"
            value={parent}
            disabled={!wholeTenant}
            onChange={(e) => setParent(e.target.value)}
            options={[{ value: "", label: t("topLevel") }, ...activeUnits.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
          />
          <NumberField
            label={t("threshold")}
            hint={t("thresholdHint")}
            name="threshold"
            min={1}
            max={100}
            value={threshold}
            disabled={!wholeTenant}
            onValueChange={(v) => setThreshold(v ?? 10)}
          />
        </div>

        <fieldset className="admin-fieldset">
          <legend>{t("aliases")}</legend>
          <p className="admin-meta">{t("aliasesHint")}</p>
          {aliases.map((a, i) => (
            <div className="admin-mapping" key={i}>
              <FormField
                label={t("aliasFrom")}
                name={`alias-from-${i}`}
                value={a.from}
                disabled={!wholeTenant}
                onChange={(e) => setAliases(aliases.map((x, j) => (j === i ? { ...x, from: e.target.value } : x)))}
              />
              <FormField
                label={t("aliasTo")}
                name={`alias-to-${i}`}
                value={a.to}
                disabled={!wholeTenant}
                onChange={(e) => setAliases(aliases.map((x, j) => (j === i ? { ...x, to: e.target.value } : x)))}
              />
              {wholeTenant ? <IconButton icon="trash" label={t("removeAlias")} onClick={() => setAliases(aliases.filter((_, j) => j !== i))} /> : null}
            </div>
          ))}
          {wholeTenant ? (
            <div className="ui-control-row">
              <Button variant="secondary" size="sm" icon="plus" onClick={() => setAliases([...aliases, { from: "", to: "" }])}>
                {t("addAlias")}
              </Button>
            </div>
          ) : null}
        </fieldset>

        <fieldset className="admin-fieldset">
          <legend>{t("valueMap")}</legend>
          <p className="admin-meta">{t("valueMapHint")}</p>
          {valueMap.map((v, i) => (
            <div className="admin-mapping" key={i}>
              <FormField
                label={t("valueKey")}
                hint={t("valueKeyHint")}
                name={`value-key-${i}`}
                dir="ltr"
                value={v.key}
                disabled={!wholeTenant}
                onChange={(e) => setValueMap(valueMap.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))}
              />
              <SelectField
                label={t("valueUnit")}
                name={`value-unit-${i}`}
                value={v.unitId}
                disabled={!wholeTenant}
                onChange={(e) => setValueMap(valueMap.map((x, j) => (j === i ? { ...x, unitId: e.target.value } : x)))}
                options={[{ value: "", label: t("chooseUnit") }, ...productUnits.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
              />
              {wholeTenant ? <IconButton icon="trash" label={t("removeValue")} onClick={() => setValueMap(valueMap.filter((_, j) => j !== i))} /> : null}
            </div>
          ))}
          {wholeTenant ? (
            <div className="ui-control-row">
              <Button variant="secondary" size="sm" icon="plus" onClick={() => setValueMap([...valueMap, { key: "", unitId: "" }])}>
                {t("addValue")}
              </Button>
            </div>
          ) : null}
        </fieldset>

        {wholeTenant ? (
          <div className="ui-control-row">
            <Button variant="primary" busy={busy === "save"} busyLabel={t("saving")} onClick={() => void save()}>
              {t("save")}
            </Button>
            <Button variant="secondary" icon="search" disabled={!mapping} busy={busy === "preview"} busyLabel={t("previewing")} onClick={() => void runPreview()}>
              {t("preview")}
            </Button>
            <Button variant="secondary" icon="check" disabled={!mapping?.previewedAt} busy={busy === "approve"} busyLabel={t("approving")} onClick={() => void approve()}>
              {t("approve")}
            </Button>
          </div>
        ) : null}
        <p className="admin-meta">{t("approveHint")}</p>
      </section>

      {preview ? (
        <section className="admin-panel" aria-labelledby="mapping-preview-title">
          <h2 id="mapping-preview-title">{preview.run.mode === "preview" ? t("previewTitle") : t("appliedTitle")}</h2>
          {preview.diff.thresholdExceeded ? <Alert tone="warning" title={t("thresholdExceeded")} live /> : null}
          <StatCards
            label={t("previewTitle")}
            stats={COUNTS.map((k) => ({ key: k, label: t(`counts.${k}`), value: String(countsOf(preview.diff)[k]) }))}
          />
          {preview.diff.create.length > 0 ? (
            <DataTable
              caption={t("createTitle")}
              rows={preview.diff.create}
              rowKey={(r) => r.key}
              density="compact"
              columns={[
                { key: "name", header: t("unitName"), rowHeader: true, cell: (r) => <bdi>{r.name}</bdi> },
                { key: "type", header: t("unitType"), cell: (r) => r.typeKey },
                {
                  key: "parent",
                  header: t("under"),
                  cell: (r) => (r.parentKey ? (preview.diff.create.find((c) => c.key === r.parentKey)?.name ?? r.parentKey) : unitName(r.parentUnitId)),
                },
              ]}
            />
          ) : null}
          {preview.diff.rename.length > 0 ? (
            <DataTable
              caption={t("renameTitle")}
              rows={preview.diff.rename}
              rowKey={(r) => r.unitId}
              density="compact"
              columns={[
                { key: "from", header: t("from"), rowHeader: true, cell: (r) => <bdi>{r.from}</bdi> },
                { key: "to", header: t("to"), cell: (r) => <bdi>{r.to}</bdi> },
              ]}
            />
          ) : null}
          {preview.diff.flagged.length > 0 ? (
            <DataTable
              caption={t("flaggedTitle")}
              rows={preview.diff.flagged}
              rowKey={(r) => r.unitId}
              density="compact"
              columns={[
                { key: "unit", header: t("unitName"), rowHeader: true, cell: (r) => unitName(r.unitId) },
                { key: "reason", header: t("reason"), cell: (r) => t(`flagReasons.${r.reason === "active_children" ? "active_children" : "active_memberships"}`) },
              ]}
            />
          ) : null}
          {preview.diff.unmapped.length > 0 ? (
            <DataTable
              caption={t("unmappedTitle")}
              rows={preview.diff.unmapped}
              rowKey={(r) => r.userId}
              density="compact"
              columns={[
                { key: "person", header: t("person"), rowHeader: true, cell: (r) => person(r.userId) },
                {
                  key: "reason",
                  header: t("reason"),
                  cell: (r) => t(`unmappedReasons.${r.reason === "ambiguous" ? "ambiguous" : r.reason === "inactive" ? "inactive" : "no_value"}`),
                },
              ]}
            />
          ) : null}
        </section>
      ) : null}

      <section className="admin-panel" aria-labelledby="mapping-runs-title">
        <h2 id="mapping-runs-title">{t("runsTitle")}</h2>
        <DataTable
          caption={t("runsTitle")}
          rows={runs}
          rowKey={(r) => r.id}
          emptyTitle={t("noRuns")}
          density="compact"
          columns={[
            { key: "started", header: t("started"), rowHeader: true, cell: (r) => when(r.startedAt, "short") },
            { key: "mode", header: t("mode"), cell: (r) => t(r.mode === "preview" ? "modes.preview" : "modes.apply") },
            {
              key: "outcome",
              header: t("outcome"),
              cell: (r) => (
                <Badge tone={r.outcome === "succeeded" ? "success" : "danger"}>{t(r.outcome === "succeeded" ? "outcomes.succeeded" : "outcomes.stopped")}</Badge>
              ),
            },
            {
              key: "summary",
              header: t("summary"),
              cell: (r) =>
                t("runSummary", {
                  create: String(r.counts.create ?? 0),
                  deactivate: String(r.counts.deactivate ?? 0),
                  added: String(r.counts.membershipsAdded ?? 0),
                  ended: String(r.counts.membershipsEnded ?? 0),
                }),
            },
          ]}
        />
        <p className="admin-meta">
          <Link href="/admin/organization">{t("backToTree")}</Link>
        </p>
      </section>
    </div>
  );
}
