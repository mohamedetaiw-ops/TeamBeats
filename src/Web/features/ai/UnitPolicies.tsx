"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { CLASSIFICATIONS, type OrgPolicySummary, type OrgPolicyView } from "@/lib/ai/types";
import type { OrgUnitView } from "@/lib/admin/types";

type Result = { ok: boolean; messages: string[] } | null;
type Draft = {
  limitModels: boolean;
  allowlist: string[];
  limitConfidential: boolean;
  confidential: string[];
  maxClassification: string;
  maxOutputTokens: string;
  minTemperature: string;
  maxTemperature: string;
};

function pick(names: Record<string, string> | undefined, locale: string): string {
  if (!names) return "";
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
}

function draftOf(view: OrgPolicyView): Draft {
  const p = view.policy;
  return {
    limitModels: p?.allowlist != null,
    allowlist: p?.allowlist ?? view.parent.allowlist,
    limitConfidential: p?.confidential != null,
    confidential: p?.confidential ?? view.parent.confidential,
    maxClassification: p?.maxClassification ?? "",
    maxOutputTokens: p?.maxOutputTokens != null ? String(p.maxOutputTokens) : "",
    minTemperature: p?.minTemperature != null ? String(p.minTemperature) : "",
    maxTemperature: p?.maxTemperature != null ? String(p.maxTemperature) : "",
  };
}

const num = (value: string) => (value.trim() === "" ? null : Number(value));

/**
 * Unit model policies (spec 013 US7, FR-009): an AI Policy Administrator of a unit, or of the whole organization,
 * narrows the models, Confidential models, highest classification, output tokens and temperature for the unit and its
 * children. The page shows what the unit inherits and from where; the API refuses anything that loosens a rule above
 * and names that rule.
 */
export function UnitPolicies({ initial, units }: { initial: OrgPolicySummary[]; units: OrgUnitView[] }) {
  const t = useTranslations("ai.units");
  const locale = useLocale();
  const [summaries, setSummaries] = useState(initial);
  const [unit, setUnit] = useState(units[0]?.id ?? "");
  const [view, setView] = useState<OrgPolicyView | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const ids = { unit: useId(), level: useId() };
  const unitName = (id: string) => pick(units.find((u) => u.id === id)?.names, locale) || t("unknownUnit");
  const sourceName = (source: string | undefined) => (!source || source === "tenant" ? t("fromTenant") : t("fromUnit", { unit: unitName(source) }));

  async function load(id: string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/ai/org-policies/${encodeURIComponent(id)}`, { cache: "no-store" });
      if (response.ok) {
        const next = (await response.json()) as OrgPolicyView;
        setView(next);
        setDraft(draftOf(next));
      } else {
        setView(null);
        setDraft(null);
        setResult({ ok: false, messages: response.status === 404 ? [t("notYours")] : await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  async function refreshList() {
    const response = await fetch("/api/v1/ai/org-policies", { cache: "no-store" });
    if (response.ok) setSummaries((await response.json()) as OrgPolicySummary[]);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!view || !draft) return;
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/ai/org-policies/${encodeURIComponent(view.unitId)}`, {
        method: "PUT",
        headers: { "content-type": "application/json", "if-match": `"v${view.version}"` },
        body: JSON.stringify({
          allowlist: draft.limitModels ? draft.allowlist : null,
          confidential: draft.limitConfidential ? draft.confidential : null,
          maxClassification: draft.maxClassification || null,
          maxOutputTokens: num(draft.maxOutputTokens),
          minTemperature: num(draft.minTemperature),
          maxTemperature: num(draft.maxTemperature),
        }),
      });
      if (response.ok) {
        const next = (await response.json()) as OrgPolicyView;
        setView(next);
        setDraft(draftOf(next));
        setResult({ ok: true, messages: [t("saved", { unit: unitName(next.unitId) })] });
        await refreshList();
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!view) return;
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/ai/org-policies/${encodeURIComponent(view.unitId)}`, { method: "DELETE" });
      if (response.ok) {
        setResult({ ok: true, messages: [t("removed", { unit: unitName(view.unitId) })] });
        await refreshList();
        await load(view.unitId);
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const toggle = (list: string[], model: string, on: boolean) => (on ? [...list.filter((m) => m !== model), model] : list.filter((m) => m !== model));

  const columns: Column<OrgPolicySummary>[] = [
    { key: "unit", header: t("unit"), rowHeader: true, cell: (s) => <bdi>{unitName(s.unitId)}</bdi> },
    {
      key: "models",
      header: t("models"),
      cell: (s) => (s.policy.allowlist ? t("modelCount", { count: s.policy.allowlist.length }) : t("inherits")),
    },
    { key: "level", header: t("maxClassification"), cell: (s) => (s.policy.maxClassification ? t(`levels.${s.policy.maxClassification}`) : t("inherits")) },
    {
      key: "open",
      header: t("actions"),
      align: "end",
      cell: (s) => (
        <Button
          variant="quiet"
          size="sm"
          aria-label={t("openFor", { unit: unitName(s.unitId) })}
          onClick={() => {
            setUnit(s.unitId);
            void load(s.unitId);
          }}
        >
          {t("open")}
        </Button>
      ),
    },
  ];

  const parentRank = view ? CLASSIFICATIONS.indexOf(view.parent.maxClassification as (typeof CLASSIFICATIONS)[number]) : -1;

  return (
    <div className="ai">
      <p>{t("intro")}</p>
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={summaries}
        rowKey={(s) => s.unitId}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />
      <form
        className="ai-section"
        onSubmit={(e) => {
          e.preventDefault();
          if (unit) void load(unit);
        }}
      >
        <div className="ai-row">
          <div className="field">
            <label htmlFor={ids.unit}>{t("chooseUnit")}</label>
            <select id={ids.unit} value={unit} onChange={(e) => setUnit(e.target.value)}>
              {units.map((u) => (
                <option key={u.id} value={u.id}>
                  {pick(u.names, locale)}
                </option>
              ))}
            </select>
          </div>
          <Button type="submit" variant="secondary" busy={busy} busyLabel={t("working")} disabled={!unit}>
            {t("open")}
          </Button>
        </div>
      </form>

      {view && draft ? (
        <form className="ai-section" aria-labelledby="ai-unit-policy" onSubmit={save} data-testid="ai-unit-policy">
          <h2 id="ai-unit-policy">{t("editTitle", { unit: unitName(view.unitId) })}</h2>
          {!view.editable ? <p className="notice">{t("readOnly")}</p> : null}
          <fieldset disabled={!view.editable || busy}>
            <legend>{t("models")}</legend>
            <p className="field-hint">{t("inheritedModels", { source: sourceName(view.source.allowlist) })}</p>
            <Checkbox label={t("limitModels")} checked={draft.limitModels} onChange={(e) => setDraft({ ...draft, limitModels: e.target.checked })} />
            {draft.limitModels
              ? view.parent.allowlist.map((m) => (
                  <Checkbox
                    key={m}
                    label={<bdi>{m}</bdi>}
                    checked={draft.allowlist.includes(m)}
                    onChange={(e) => setDraft({ ...draft, allowlist: toggle(draft.allowlist, m, e.target.checked) })}
                  />
                ))
              : null}
          </fieldset>
          {view.parent.confidential.length > 0 ? (
            <fieldset disabled={!view.editable || busy}>
              <legend>{t("confidential")}</legend>
              <p className="field-hint">{t("inheritedConfidential", { source: sourceName(view.source.confidential) })}</p>
              <Checkbox label={t("limitConfidential")} checked={draft.limitConfidential} onChange={(e) => setDraft({ ...draft, limitConfidential: e.target.checked })} />
              {draft.limitConfidential
                ? view.parent.confidential.map((m) => (
                    <Checkbox
                      key={m}
                      label={<bdi>{m}</bdi>}
                      checked={draft.confidential.includes(m)}
                      onChange={(e) => setDraft({ ...draft, confidential: toggle(draft.confidential, m, e.target.checked) })}
                    />
                  ))
                : null}
            </fieldset>
          ) : null}
          <div className="ai-row">
            <div className="field">
              <label htmlFor={ids.level}>{t("maxClassification")}</label>
              <p id={`${ids.level}-hint`} className="field-hint">
                {t("inheritedLevel", { level: t(`levels.${view.parent.maxClassification}`), source: sourceName(view.source.maxClassification) })}
              </p>
              <select
                id={ids.level}
                aria-describedby={`${ids.level}-hint`}
                value={draft.maxClassification}
                disabled={!view.editable || busy}
                onChange={(e) => setDraft({ ...draft, maxClassification: e.target.value })}
              >
                <option value="">{t("inherits")}</option>
                {CLASSIFICATIONS.filter((_, i) => i <= parentRank).map((c) => (
                  <option key={c} value={c}>
                    {t(`levels.${c}`)}
                  </option>
                ))}
              </select>
            </div>
            <FormField
              label={t("maxOutputTokens")}
              hint={view.parent.maxOutputTokens != null ? t("inheritedTokens", { count: view.parent.maxOutputTokens }) : t("noTokenLimit")}
              name="maxOutputTokens"
              type="number"
              min={1}
              value={draft.maxOutputTokens}
              disabled={!view.editable || busy}
              onChange={(e) => setDraft({ ...draft, maxOutputTokens: e.target.value })}
            />
            <FormField
              label={t("minTemperature")}
              name="minTemperature"
              type="number"
              min={0}
              max={2}
              step={0.1}
              value={draft.minTemperature}
              disabled={!view.editable || busy}
              onChange={(e) => setDraft({ ...draft, minTemperature: e.target.value })}
            />
            <FormField
              label={t("maxTemperature")}
              name="maxTemperature"
              type="number"
              min={0}
              max={2}
              step={0.1}
              value={draft.maxTemperature}
              disabled={!view.editable || busy}
              onChange={(e) => setDraft({ ...draft, maxTemperature: e.target.value })}
            />
          </div>
          {view.editable ? (
            <div className="button-row">
              <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
                {t("save")}
              </Button>
              {view.policy ? (
                <Button variant="destructive" disabled={busy} onClick={() => void remove()}>
                  {t("remove")}
                </Button>
              ) : null}
            </div>
          ) : null}
        </form>
      ) : null}

      <div aria-live="polite">
        {result ? (
          <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>
    </div>
  );
}
