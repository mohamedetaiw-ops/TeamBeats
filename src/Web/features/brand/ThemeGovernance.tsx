"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { FormField, SelectField } from "@/components/ui/FormField";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import type { TokenValue } from "@/lib/brand/types";

export type DriftFinding = {
  kind: string;
  severity: "error" | "warning";
  scope: string | null;
  scopeNames: Record<string, string> | null;
  brandId: string | null;
  brandName: string | null;
  token: string | null;
  slot: string | null;
  mode: string | null;
  against: string | null;
  ratio: number | null;
  required: number | null;
};

export type DriftReport = { generatedAt: string; scopes: number; findings: DriftFinding[] };

export type TokenExtension = {
  id: string;
  scope: string;
  key: string;
  token: string;
  cssName: string;
  type: string;
  value: TokenValue;
  description: string | null;
  state: "proposed" | "approved" | "rejected";
  proposedBy: string;
  reviewNote: string | null;
  inherited: boolean;
};

type Scope = { value: string; label: string };

/** The kinds the API reports; each has its own words and remediation. */
const KINDS = ["asset_missing", "asset_noncompliant", "token_unknown", "token_invalid", "override_locked", "token_unresolved", "inaccessible"] as const;
const TYPES = ["color", "dimension", "number", "duration", "shadow"] as const;
const STATE_TONES = { proposed: "warning", approved: "success", rejected: "danger" } as const;

/**
 * Theme governance (spec 033 R2 T022, US8, FR-013 to FR-015): the drift report with what to do about each finding, the
 * brand's tokens as a DTCG file to export and import, and reviewed token extensions per scope. The API decides who may
 * see, propose, review and remove; this screen shows its answers and sends what was chosen.
 */
export function ThemeGovernance({
  report,
  brand,
  scopes,
  initialScope,
  initialExtensions,
  extensionsAvailable,
}: {
  report: DriftReport | null;
  brand: { id: string; name: string } | null;
  scopes: Scope[];
  initialScope: string;
  initialExtensions: TokenExtension[];
  extensionsAvailable: boolean;
}) {
  const t = useTranslations("brand.governance");
  const locale = useLocale();
  const { busy, result, run } = useAdminAction("/admin/branding/governance");
  const [scope, setScope] = useState(initialScope);
  const [extensions, setExtensions] = useState(initialExtensions);
  const [file, setFile] = useState<UploadItem[]>([]);
  const [text, setText] = useState<string | null>(null);
  const [draft, setDraft] = useState({ key: "", type: "color", value: "", dark: "", description: "" });
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 2 });

  const where = (f: DriftFinding) =>
    f.brandName !== null
      ? t("inBrand", { brand: f.brandName })
      : f.scope === "tenant"
        ? t("tenant")
        : (f.scopeNames?.[locale] ?? f.scopeNames?.en ?? t("unknownScope"));
  const kindLabel = (kind: string) => ((KINDS as readonly string[]).includes(kind) ? t(`kinds.${kind}`) : kind);
  const remedy = (kind: string) => ((KINDS as readonly string[]).includes(kind) ? t(`remedies.${kind}`) : "");
  const detail = (f: DriftFinding) =>
    f.kind === "inaccessible" && f.ratio !== null && f.required !== null
      ? t("contrastDetail", { against: f.against ?? "", mode: t(`modes.${f.mode ?? "light"}`), ratio: number.format(f.ratio), required: number.format(f.required) })
      : f.slot
        ? t("slotDetail", { slot: f.slot })
        : "";

  async function loadScope(next: string) {
    setScope(next);
    const response = await fetch(`/api/v1/experience/token-extensions?scope=${encodeURIComponent(next)}`, { cache: "no-store" });
    setExtensions(response.ok ? ((await response.json()) as { items: TokenExtension[] }).items : []);
  }

  async function addFile(files: File[]) {
    const chosen = files[0];
    if (!chosen) return;
    setFile([{ key: chosen.name, name: chosen.name, size: chosen.size }]);
    setText(await chosen.text());
  }

  async function importFile() {
    if (!brand || text === null) return;
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      setFile((items) => items.map((i) => ({ ...i, error: t("notJson") })));
      return;
    }
    const outcome = await run("import", "PUT", `/api/v1/experience/brands/${brand.id}/tokens`, parsed, t("imported", { brand: brand.name }));
    if (outcome.ok) {
      setFile([]);
      setText(null);
    }
  }

  async function propose() {
    const modes = draft.dark.trim() && (draft.type === "color" || draft.type === "shadow") ? { dark: draft.dark.trim() } : undefined;
    const outcome = await run<TokenExtension>(
      "propose",
      "POST",
      "/api/v1/experience/token-extensions",
      { scope, key: draft.key.trim(), type: draft.type, value: { $value: draft.value.trim(), $modes: modes }, description: draft.description },
      t("proposed", { key: draft.key.trim() }),
    );
    if (outcome.ok) {
      setExtensions((list) => [...list, outcome.data]);
      setDraft({ key: "", type: draft.type, value: "", dark: "", description: "" });
    }
  }

  async function review(x: TokenExtension, approve: boolean) {
    const outcome = await run<TokenExtension>(`review-${x.id}`, "POST", `/api/v1/experience/token-extensions/${x.id}/review`, { approve },
      approve ? t("approved", { key: x.key }) : t("rejected", { key: x.key }));
    if (outcome.ok) setExtensions((list) => list.map((e) => (e.id === x.id ? outcome.data : e)));
  }

  async function remove(x: TokenExtension) {
    const outcome = await run(`remove-${x.id}`, "DELETE", `/api/v1/experience/token-extensions/${x.id}`, undefined, t("removed", { key: x.key }));
    if (outcome.ok) setExtensions((list) => list.filter((e) => e.id !== x.id));
  }

  const findings = report?.findings ?? [];
  const errors = findings.filter((f) => f.severity === "error").length;

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />

      <section className="admin-panel" aria-labelledby="drift-title">
        <div className="admin-panel-head">
          <h2 id="drift-title">{t("driftTitle")}</h2>
        </div>
        <p className="admin-meta">
          {report ? t("driftSummary", { findings: findings.length, errors, scopes: report.scopes }) : t("driftUnavailable")}
        </p>
        <DataTable
          caption={t("driftTitle")}
          captionHidden
          rows={findings}
          rowKey={(f) => [f.kind, f.scope, f.brandId, f.token, f.slot, f.mode, f.against].join("|")}
          emptyTitle={t("driftEmptyTitle")}
          emptyMessage={t("driftEmptyMessage")}
          columns={[
            { key: "kind", header: t("problem"), rowHeader: true, cell: (f) => kindLabel(f.kind), sortValue: (f) => kindLabel(f.kind) },
            {
              key: "severity",
              header: t("severity"),
              nowrap: true,
              cell: (f) => <Badge tone={f.severity === "error" ? "danger" : "warning"}>{t(`severities.${f.severity}`)}</Badge>,
              sortValue: (f) => (f.severity === "error" ? 0 : 1),
            },
            { key: "where", header: t("where"), cell: (f) => <bdi>{where(f)}</bdi>, sortValue: (f) => where(f) },
            { key: "token", header: t("token"), cell: (f) => (f.token ? <code dir="ltr">{f.token}</code> : detail(f)) },
            { key: "detail", header: t("detail"), priority: "low", cell: (f) => (f.token ? detail(f) : "") },
            { key: "remedy", header: t("remedy"), cell: (f) => remedy(f.kind) },
          ]}
        />
      </section>

      {brand ? (
        <section className="admin-panel" aria-labelledby="token-file-title">
          <div className="admin-panel-head">
            <h2 id="token-file-title">{t("fileTitle", { brand: brand.name })}</h2>
          </div>
          <p className="admin-meta">{t("fileIntro")}</p>
          <div className="ui-control-row">
            <ButtonLink href={`/api/v1/experience/brands/${brand.id}/tokens`} variant="secondary" icon="download">
              {t("export")}
            </ButtonLink>
          </div>
          <FileUpload
            label={t("importLabel")}
            hint={t("importHint")}
            accept=".json,application/json,application/design-tokens+json"
            multiple={false}
            items={file}
            onAdd={(files) => void addFile(files)}
            onRemove={() => {
              setFile([]);
              setText(null);
            }}
          />
          <div className="ui-control-row">
            <Button variant="primary" disabled={busy !== null || text === null} onClick={() => void importFile()}>
              {t("import")}
            </Button>
          </div>
        </section>
      ) : null}

      <section className="admin-panel" aria-labelledby="extensions-title">
        <div className="admin-panel-head">
          <h2 id="extensions-title">{t("extensionsTitle")}</h2>
        </div>
        <p className="admin-meta">{extensionsAvailable ? t("extensionsIntro") : t("extensionsUnavailable")}</p>
        <SelectField label={t("scope")} name="scope" value={scope} onChange={(e) => void loadScope(e.target.value)} options={scopes} />
        <DataTable
          caption={t("extensionsTitle")}
          captionHidden
          rows={extensions}
          rowKey={(x) => x.id}
          emptyTitle={t("extensionsEmptyTitle")}
          emptyMessage={t("extensionsEmptyMessage")}
          columns={[
            { key: "key", header: t("cssName"), rowHeader: true, cell: (x) => <code dir="ltr">{x.cssName}</code>, sortValue: (x) => x.key },
            { key: "type", header: t("type"), nowrap: true, cell: (x) => t(`types.${x.type}`) },
            { key: "value", header: t("value"), cell: (x) => <code dir="ltr">{x.value.$value}</code> },
            {
              key: "state",
              header: t("state"),
              nowrap: true,
              cell: (x) => <Badge tone={STATE_TONES[x.state]}>{x.inherited ? t("inherited") : t(`states.${x.state}`)}</Badge>,
            },
            { key: "description", header: t("description"), priority: "low", cell: (x) => x.description ?? "" },
            {
              key: "actions",
              header: t("actions"),
              cell: (x) =>
                x.inherited ? null : (
                  <div className="ui-control-row">
                    {x.state === "proposed" ? (
                      <>
                        <Button variant="secondary" disabled={busy !== null} onClick={() => void review(x, true)} aria-label={t("approveFor", { key: x.key })}>
                          {t("approve")}
                        </Button>
                        <Button variant="secondary" disabled={busy !== null} onClick={() => void review(x, false)} aria-label={t("rejectFor", { key: x.key })}>
                          {t("reject")}
                        </Button>
                      </>
                    ) : null}
                    <ConfirmAction
                      label={t("remove")}
                      question={t("removeQuestion", { key: x.key })}
                      confirmLabel={t("removeConfirm")}
                      cancelLabel={t("cancel")}
                      onConfirm={() => void remove(x)}
                    />
                  </div>
                ),
            },
          ]}
        />
        {extensionsAvailable ? (
          <fieldset className="admin-fieldset">
            <legend>{t("proposeTitle")}</legend>
            <div className="admin-fields">
              <FormField label={t("key")} hint={t("keyHint")} name="extension-key" dir="ltr" value={draft.key} onChange={(e) => setDraft({ ...draft, key: e.target.value })} />
              <SelectField
                label={t("type")}
                name="extension-type"
                value={draft.type}
                onChange={(e) => setDraft({ ...draft, type: e.target.value })}
                options={TYPES.map((type) => ({ value: type, label: t(`types.${type}`) }))}
              />
              <FormField label={t("value")} hint={t("valueHint")} name="extension-value" dir="ltr" value={draft.value} onChange={(e) => setDraft({ ...draft, value: e.target.value })} />
              {draft.type === "color" || draft.type === "shadow" ? (
                <FormField label={t("darkValue")} name="extension-dark" dir="ltr" value={draft.dark} onChange={(e) => setDraft({ ...draft, dark: e.target.value })} />
              ) : null}
              <FormField
                label={t("description")}
                name="extension-description"
                maxLength={200}
                value={draft.description}
                onChange={(e) => setDraft({ ...draft, description: e.target.value })}
              />
            </div>
            <div className="ui-control-row">
              <Button variant="primary" disabled={busy !== null || draft.key.trim() === "" || draft.value.trim() === ""} onClick={() => void propose()}>
                {t("propose")}
              </Button>
            </div>
          </fieldset>
        ) : null}
      </section>
    </div>
  );
}
