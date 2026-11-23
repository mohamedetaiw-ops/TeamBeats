"use client";

import { useId, useState, type FormEvent } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable, type Column } from "@/components/ui/DataTable";
import {
  unitName,
  type AudienceRuleSummary,
  type ComponentChoice,
  type FeatureControl,
  type FeatureControlVersion,
  type OrgUnit,
} from "@/lib/experience/builder-types";
import { useHydrated } from "@/lib/clock";
import { SendMessages, useSend } from "./useSend";

type Kind = FeatureControl["subjectKind"];
type ScopeKind = "tenant" | "unit" | "audience";

function label(names: Record<string, string>, locale: string, fallback: string): string {
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? fallback;
}

/**
 * Experience feature controls (spec 032 R2 T030, FR-057): switch a component or a template on or off for the whole
 * organization, a unit and the units below it, or (components only) an audience. Every change is a kept version. The most
 * specific switch wins; the package and the tenant's own component choice still apply, as the API decides.
 */
export function FeatureControlsAdmin({
  initial,
  components,
  templates,
  units,
  audiences,
}: {
  initial: FeatureControl[];
  components: ComponentChoice[];
  templates: { id: string; name: string }[];
  units: OrgUnit[];
  audiences: AudienceRuleSummary[];
}) {
  const t = useTranslations("builder.manage.controls");
  const locale = useLocale();
  const format = useFormatter();
  const hydrated = useHydrated();
  const ids = { title: useId(), kind: useId(), subject: useId(), scope: useId(), target: useId(), state: useId(), history: useId() };
  const [controls, setControls] = useState(initial);
  const [kind, setKind] = useState<Kind>("component");
  const [subject, setSubject] = useState("");
  const [scope, setScope] = useState<ScopeKind>("tenant");
  const [target, setTarget] = useState("");
  const [enabled, setEnabled] = useState(false);
  const [history, setHistory] = useState<{ control: FeatureControl; versions: FeatureControlVersion[] } | null>(null);
  const { busy, result, send } = useSend(t("failed"));

  const when = (iso: string) => (hydrated ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "");
  const subjectName = (c: FeatureControl) =>
    c.subjectKind === "component"
      ? (components.find((x) => x.componentId === c.subjectId)?.name ?? c.subjectId)
      : (templates.find((x) => x.id === c.subjectId)?.name ?? c.subjectId);
  const scopeName = (c: FeatureControl) => {
    if (c.audienceId) {
      const rule = audiences.find((a) => a.id === c.audienceId);
      return t("audienceScope", { name: rule ? label(rule.labels, locale, t("unknownAudience")) : t("unknownAudience") });
    }
    if (c.orgUnitId) {
      const unit = units.find((u) => u.id === c.orgUnitId);
      return unit ? unitName(unit, locale) : t("unknownUnit");
    }
    return t("wholeTenant");
  };

  async function reload() {
    const response = await fetch("/api/v1/experience/feature-controls", { cache: "no-store" });
    if (response.ok) setControls((await response.json()) as FeatureControl[]);
  }

  async function showHistory(c: FeatureControl) {
    const response = await fetch(`/api/v1/experience/feature-controls/${c.id}/versions`, { cache: "no-store" }).catch(() => null);
    if (response?.ok) setHistory({ control: c, versions: (await response.json()) as FeatureControlVersion[] });
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    const saved = await send(
      "/api/v1/experience/feature-controls",
      "PUT",
      {
        subjectKind: kind,
        subjectId: subject,
        orgUnitId: scope === "unit" ? target || null : null,
        audienceId: scope === "audience" ? target || null : null,
        enabled,
      },
      t("saved"),
    );
    if (saved) {
      await reload();
      setSubject("");
      setTarget("");
    }
  }

  const columns: Column<FeatureControl>[] = [
    { key: "subject", header: t("subject"), rowHeader: true, cell: (c) => <bdi>{subjectName(c)}</bdi> },
    { key: "kind", header: t("kind"), priority: "low", cell: (c) => t(`kinds.${c.subjectKind}`) },
    { key: "scope", header: t("scope"), cell: (c) => <bdi>{scopeName(c)}</bdi> },
    { key: "state", header: t("state"), cell: (c) => <Badge tone={c.enabled ? "success" : "warning"}>{c.enabled ? t("on") : t("off")}</Badge> },
    { key: "changed", header: t("changed"), priority: "low", cell: (c) => when(c.changedAt) },
    {
      key: "manage",
      header: t("manage"),
      align: "end",
      cell: (c) => (
        <div className="button-row">
          <Button variant="quiet" size="sm" aria-label={t("historyLabel", { name: subjectName(c) })} onClick={() => void showHistory(c)}>
            {t("history")}
          </Button>
          <ConfirmAction
            label={t("remove")}
            question={t("removeQuestion", { name: subjectName(c), scope: scopeName(c) })}
            confirmLabel={t("removeConfirm")}
            cancelLabel={t("cancel")}
            onConfirm={async () => {
              if (await send(`/api/v1/experience/feature-controls/${c.id}`, "DELETE", null, t("removed"))) await reload();
            }}
          />
        </div>
      ),
    },
  ];

  const subjects = kind === "component" ? components.map((c) => ({ id: c.componentId, name: c.name ?? c.componentId })) : templates;

  return (
    <div className="builder-manage" data-testid="feature-controls">
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={controls}
        rowKey={(c) => c.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />

      {history ? (
        <section className="builder-manage-form" aria-labelledby={ids.history} data-testid="feature-control-history">
          <h2 id={ids.history}>{t("historyTitle", { name: subjectName(history.control) })}</h2>
          <ol className="builder-manage-list">
            {history.versions.map((v) => (
              <li key={v.version}>
                {t("versionLine", {
                  version: String(v.version),
                  state: v.enabled === null ? t("removedState") : v.enabled ? t("on") : t("off"),
                  when: when(v.changedAt),
                })}
              </li>
            ))}
          </ol>
          <Button variant="secondary" onClick={() => setHistory(null)}>
            {t("close")}
          </Button>
        </section>
      ) : null}

      <form className="builder-manage-form" aria-labelledby={ids.title} onSubmit={(e) => void submit(e)} data-testid="feature-control-form">
        <h2 id={ids.title}>{t("addTitle")}</h2>
        <p className="field-hint">{t("addHint")}</p>
        <div className="builder-manage-row">
          <div className="field">
            <label htmlFor={ids.kind}>{t("kind")}</label>
            <select
              id={ids.kind}
              value={kind}
              onChange={(e) => {
                setKind(e.target.value as Kind);
                setSubject("");
                if (e.target.value === "template" && scope === "audience") setScope("tenant");
              }}
            >
              <option value="component">{t("kinds.component")}</option>
              <option value="template">{t("kinds.template")}</option>
            </select>
          </div>
          <div className="field">
            <label htmlFor={ids.subject}>{t("subject")}</label>
            <select id={ids.subject} value={subject} onChange={(e) => setSubject(e.target.value)} required>
              <option value="">{t("choose")}</option>
              {subjects.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </div>
        </div>
        <div className="builder-manage-row">
          <div className="field">
            <label htmlFor={ids.scope}>{t("scope")}</label>
            <select
              id={ids.scope}
              value={scope}
              onChange={(e) => {
                setScope(e.target.value as ScopeKind);
                setTarget("");
              }}
            >
              <option value="tenant">{t("wholeTenant")}</option>
              <option value="unit">{t("unitScope")}</option>
              {kind === "component" ? <option value="audience">{t("audienceOption")}</option> : null}
            </select>
          </div>
          {scope !== "tenant" ? (
            <div className="field">
              <label htmlFor={ids.target}>{scope === "unit" ? t("unit") : t("audience")}</label>
              <select id={ids.target} value={target} onChange={(e) => setTarget(e.target.value)} required>
                <option value="">{t("choose")}</option>
                {scope === "unit"
                  ? units.map((u) => (
                      <option key={u.id} value={u.id}>
                        {unitName(u, locale)}
                      </option>
                    ))
                  : audiences.map((a) => (
                      <option key={a.id} value={a.id}>
                        {label(a.labels, locale, a.id)}
                      </option>
                    ))}
              </select>
            </div>
          ) : null}
          <div className="field">
            <label htmlFor={ids.state}>{t("state")}</label>
            <select id={ids.state} value={enabled ? "on" : "off"} onChange={(e) => setEnabled(e.target.value === "on")}>
              <option value="off">{t("off")}</option>
              <option value="on">{t("on")}</option>
            </select>
          </div>
        </div>
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
          {t("save")}
        </Button>
      </form>
      <SendMessages result={result} />
    </div>
  );
}
