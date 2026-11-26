"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, FormPanel, SelectField } from "@/components/ui/FormField";
import { KeyName } from "@/components/ui/KeyName";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import { keyName } from "@/lib/admin/keys";
import type { ContrastFinding, TokenSchemaEntry, TokenValue } from "@/lib/brand/types";

export type TenantDesign = {
  id: string;
  key: string;
  base: string;
  names: Record<string, string>;
  description: string | null;
  tokens: Record<string, TokenValue>;
  state: "draft" | "proposed" | "approved" | "rejected" | "retired";
  reviewNote: string | null;
  findings: ContrastFinding[];
};

type Row = { token: string; value: string; dark: string };

type Editor = { id: string | null; key: string; base: string; en: string; ar: string; description: string; rows: Row[] };

const BASES = ["beats", "atlas", "pulse", "studio"] as const;
const STATE_TONES = { draft: "neutral", proposed: "warning", approved: "success", rejected: "danger", retired: "neutral" } as const;
const EMPTY: Editor = { id: null, key: "", base: "beats", en: "", ar: "", description: "", rows: [{ token: "", value: "", dark: "" }] };

/**
 * Tenant designs (spec 036 R3, DLS-120 to DLS-122): designs derived from one of the four, put up for review, approved by
 * someone else and applied to a brand's draft. The API decides who may save, review, retire and apply, and checks
 * contrast; this screen shows its answers and sends what was chosen.
 */
export function TenantDesigns({
  initial,
  available,
  limit,
  schema,
  brands,
}: {
  initial: TenantDesign[];
  available: boolean;
  limit: number;
  schema: TokenSchemaEntry[];
  brands: { id: string; name: string }[];
}) {
  const t = useTranslations("brand.designs");
  const tb = useTranslations("brand");
  const locale = useLocale();
  const { busy, result, run } = useAdminAction("/admin/branding/designs");
  const [designs, setDesigns] = useState(initial);
  const [editor, setEditor] = useState<Editor>(EMPTY);
  const [brand, setBrand] = useState(brands[0]?.id ?? "");
  const tokens = schema.filter((s) => s.name !== "design.preset");
  const darkable = (name: string) => schema.find((s) => s.name === name)?.modes.includes("dark") ?? false;
  const name = (d: TenantDesign) => d.names[locale] ?? d.names.en ?? d.key;
  const replace = (d: TenantDesign) => setDesigns((list) => (list.some((x) => x.id === d.id) ? list.map((x) => (x.id === d.id ? d : x)) : [...list, d]));

  function edit(d: TenantDesign) {
    setEditor({
      id: d.id,
      key: d.key,
      base: d.base,
      en: d.names.en ?? "",
      ar: d.names.ar ?? "",
      description: d.description ?? "",
      rows: Object.entries(d.tokens).map(([token, v]) => ({ token, value: v.$value, dark: v.$modes?.dark ?? "" })),
    });
  }

  function setRow(index: number, change: Partial<Row>) {
    setEditor((e) => ({ ...e, rows: e.rows.map((r, i) => (i === index ? { ...r, ...change } : r)) }));
  }

  async function save() {
    const values = Object.fromEntries(
      editor.rows
        .filter((r) => r.token && r.value.trim())
        .map((r) => [r.token, { $value: r.value.trim(), $modes: r.dark.trim() && darkable(r.token) ? { dark: r.dark.trim() } : undefined }]),
    );
    const body = { key: editor.key.trim(), base: editor.base, names: { en: editor.en, ar: editor.ar }, description: editor.description, tokens: values };
    const outcome = editor.id
      ? await run<TenantDesign>("save", "PUT", `/api/v1/experience/designs/${editor.id}`, body, (d) => t("saved", { name: name(d) }))
      : await run<TenantDesign>("save", "POST", "/api/v1/experience/designs", body, (d) => t("saved", { name: name(d) }));
    if (outcome.ok) {
      replace(outcome.data);
      setEditor(EMPTY);
    }
  }

  async function act(d: TenantDesign, action: "propose" | "approve" | "reject" | "retire") {
    const path = `/api/v1/experience/designs/${d.id}/${action === "approve" || action === "reject" ? "review" : action}`;
    const body = action === "approve" ? { approve: true } : action === "reject" ? { approve: false } : undefined;
    const outcome = await run<TenantDesign>(`${action}-${d.id}`, "POST", path, body, t(`done.${action}`, { name: name(d) }));
    if (outcome.ok) replace(outcome.data);
  }

  async function apply(d: TenantDesign) {
    const target = brands.find((b) => b.id === brand);
    if (!target) return;
    await run(`apply-${d.id}`, "POST", `/api/v1/experience/designs/${d.id}/apply`, { brandId: target.id }, t("applied", { name: name(d), brand: target.name }));
  }

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />

      <section className="admin-panel" aria-labelledby="designs-title">
        <div className="admin-panel-head">
          <h2 id="designs-title">{t("listTitle")}</h2>
        </div>
        <p className="admin-meta">{available ? t("listIntro", { limit }) : t("unavailable")}</p>
        {brands.length > 0 ? (
          <SelectField label={t("applyTo")} name="design-brand" value={brand} onChange={(e) => setBrand(e.target.value)}
            options={brands.map((b) => ({ value: b.id, label: b.name }))} />
        ) : null}
        <DataTable
          caption={t("listTitle")}
          captionHidden
          rows={designs}
          rowKey={(d) => d.id}
          emptyTitle={t("emptyTitle")}
          emptyMessage={t("emptyMessage")}
          columns={[
            { key: "name", header: t("name"), rowHeader: true, cell: (d) => <KeyName name={name(d)} code={d.key} />, sortValue: (d) => name(d) },
            { key: "base", header: t("base"), nowrap: true, cell: (d) => tb(`design.names.${d.base}`) },
            { key: "state", header: t("state"), nowrap: true, cell: (d) => <Badge tone={STATE_TONES[d.state]}>{t(`states.${d.state}`)}</Badge> },
            { key: "tokens", header: t("values"), nowrap: true, cell: (d) => t("valueCount", { count: Object.keys(d.tokens).length }) },
            {
              key: "findings",
              header: t("contrast"),
              priority: "low",
              cell: (d) => (d.findings.some((f) => f.severity === "blocking") ? <Badge tone="danger">{t("blocked")}</Badge> : <Badge tone="success">{t("passes")}</Badge>),
            },
            { key: "note", header: t("note"), priority: "low", cell: (d) => d.reviewNote ?? "" },
            {
              key: "actions",
              header: t("actions"),
              cell: (d) => (
                <div className="ui-control-row">
                  {d.state !== "retired" && available ? (
                    <Button variant="secondary" disabled={busy !== null} onClick={() => edit(d)} aria-label={t("editFor", { name: name(d) })}>
                      {t("edit")}
                    </Button>
                  ) : null}
                  {(d.state === "draft" || d.state === "rejected") && available ? (
                    <Button variant="secondary" disabled={busy !== null} onClick={() => void act(d, "propose")} aria-label={t("proposeFor", { name: name(d) })}>
                      {t("propose")}
                    </Button>
                  ) : null}
                  {d.state === "proposed" ? (
                    <>
                      <Button variant="secondary" disabled={busy !== null} onClick={() => void act(d, "approve")} aria-label={t("approveFor", { name: name(d) })}>
                        {t("approve")}
                      </Button>
                      <Button variant="secondary" disabled={busy !== null} onClick={() => void act(d, "reject")} aria-label={t("rejectFor", { name: name(d) })}>
                        {t("reject")}
                      </Button>
                    </>
                  ) : null}
                  {d.state === "approved" && brands.length > 0 ? (
                    <Button variant="primary" disabled={busy !== null} onClick={() => void apply(d)} aria-label={t("applyFor", { name: name(d) })}>
                      {t("apply")}
                    </Button>
                  ) : null}
                  {d.state !== "retired" ? (
                    <ConfirmAction
                      label={t("retire")}
                      question={t("retireQuestion", { name: name(d) })}
                      confirmLabel={t("retireConfirm")}
                      cancelLabel={t("cancel")}
                      onConfirm={() => void act(d, "retire")}
                    />
                  ) : null}
                </div>
              ),
            },
          ]}
        />
        <p className="admin-meta">{t("applyHint")}</p>
        <div className="ui-control-row">
          <ButtonLink href={brand ? `/admin/branding?brand=${encodeURIComponent(brand)}` : "/admin/branding"} variant="secondary" icon="edit">
            {t("openBrand")}
          </ButtonLink>
        </div>
      </section>

      {available ? (
        <FormPanel as="fieldset">
          <legend>{editor.id ? t("editTitle", { key: editor.key }) : t("newTitle")}</legend>
          <div className="admin-fields">
            {editor.id ? null : (
              <FormField label={t("key")} hint={t("keyHint")} name="design-key" dir="ltr" value={editor.key} onChange={(e) => setEditor({ ...editor, key: e.target.value })} />
            )}
            <SelectField label={t("base")} name="design-base" value={editor.base} onChange={(e) => setEditor({ ...editor, base: e.target.value })}
              options={BASES.map((b) => ({ value: b, label: tb(`design.names.${b}`) }))} />
            <FormField label={t("nameEn")} name="design-name-en" dir="ltr" lang="en" maxLength={60} value={editor.en} onChange={(e) => setEditor({ ...editor, en: e.target.value })} />
            <FormField label={t("nameAr")} name="design-name-ar" dir="rtl" lang="ar" maxLength={60} value={editor.ar} onChange={(e) => setEditor({ ...editor, ar: e.target.value })} />
            <FormField label={t("description")} name="design-description" maxLength={200} value={editor.description}
              onChange={(e) => setEditor({ ...editor, description: e.target.value })} />
          </div>
          {editor.rows.map((row, i) => (
            <div className="admin-fields" key={i} data-testid="design-row">
              <SelectField
                label={t("token")}
                name={`design-token-${i}`}
                value={row.token}
                onChange={(e) => setRow(i, { token: e.target.value })}
                options={[{ value: "", label: t("chooseToken") }, ...tokens.map((s) => ({ value: s.name, label: keyName(tb, "tokenNames", s.name) }))]}
              />
              <FormField label={t("value")} name={`design-value-${i}`} dir="ltr" value={row.value} onChange={(e) => setRow(i, { value: e.target.value })} />
              {row.token && darkable(row.token) ? (
                <FormField label={t("darkValue")} name={`design-dark-${i}`} dir="ltr" value={row.dark} onChange={(e) => setRow(i, { dark: e.target.value })} />
              ) : null}
              {editor.rows.length > 1 ? (
                <Button variant="secondary" icon="close" onClick={() => setEditor({ ...editor, rows: editor.rows.filter((_, j) => j !== i) })}
                  aria-label={t("removeValue", { index: i + 1 })}>
                  {t("remove")}
                </Button>
              ) : null}
            </div>
          ))}
          <div className="ui-control-row">
            <Button variant="secondary" icon="plus" onClick={() => setEditor({ ...editor, rows: [...editor.rows, { token: "", value: "", dark: "" }] })}>
              {t("addValue")}
            </Button>
            <Button variant="primary" disabled={busy !== null || (!editor.id && editor.key.trim() === "")} onClick={() => void save()}>
              {t("save")}
            </Button>
            {editor.id ? (
              <Button variant="secondary" onClick={() => setEditor(EMPTY)}>
                {t("cancel")}
              </Button>
            ) : null}
          </div>
        </FormPanel>
      ) : null}
    </div>
  );
}
