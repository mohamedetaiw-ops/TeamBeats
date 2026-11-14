"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { Switch } from "@/components/ui/Switch";
import { problemMessages } from "@/lib/notifications/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { pick } from "@/lib/services/types";
import { LETTER_PLACEHOLDERS, letterTone, type Letter, type LetterSettings, type LetterType } from "@/lib/letters";
import "./letters.css";

type FieldDraft = { key: string; en: string; ar: string };

type Draft = {
  key: string;
  nameEn: string;
  nameAr: string;
  templateEn: string;
  templateAr: string;
  fields: FieldDraft[];
  needsApproval: boolean;
  active: boolean;
  unit: string;
  eTag: string | null;
};

const empty: Draft = { key: "", nameEn: "", nameAr: "", templateEn: "", templateAr: "", fields: [], needsApproval: true, active: true, unit: "", eTag: null };

const toDraft = (type: LetterType): Draft => ({
  key: type.key,
  nameEn: type.names.en ?? "",
  nameAr: type.names.ar ?? "",
  templateEn: type.templates.en ?? "",
  templateAr: type.templates.ar ?? "",
  fields: type.manualFields.map((f) => ({ key: f.key, en: f.names.en ?? "", ar: f.names.ar ?? "" })),
  needsApproval: type.needsApproval,
  active: type.active,
  unit: type.orgUnitId ?? "",
  eTag: type.eTag,
});

/**
 * Letter types, the verification page setting and the letters of the people HR covers (spec 022 FR-021, FR-022).
 * Templates are plain text with placeholders in braces; the API checks every placeholder, who may change which type
 * and which letters each officer may see.
 */
export function LetterAdmin({
  types: initialTypes,
  settings: initialSettings,
  letters,
  units,
}: {
  types: LetterType[];
  settings: LetterSettings;
  letters: Letter[];
  units: OrgUnit[];
}) {
  const t = useTranslations("letters.admin");
  const tl = useTranslations("letters");
  const locale = useLocale();
  const format = useFormatter();
  const [types, setTypes] = useState(initialTypes);
  const [settings, setSettings] = useState(initialSettings);
  const [draft, setDraft] = useState<Draft>(empty);
  const [busy, setBusy] = useState<"type" | "settings" | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  const unitLabel = (id: string | null) => {
    const unit = id ? units.find((u) => u.id === id) : undefined;
    return unit ? unitName(unit, locale) : id ? id : t("wholeOrganization");
  };
  const patch = (p: Partial<Draft>) => setDraft((d) => ({ ...d, ...p }));
  const patchField = (i: number, p: Partial<FieldDraft>) => setDraft((d) => ({ ...d, fields: d.fields.map((f, j) => (j === i ? { ...f, ...p } : f)) }));

  async function run(kind: "type" | "settings", action: () => Promise<Response>, done: (response: Response) => Promise<void>) {
    setBusy(kind);
    setNotice(null);
    setMessages([]);
    try {
      const response = await action();
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      await done(response);
      setNotice(t("saved"));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function saveType() {
    const headers: Record<string, string> = { "content-type": "application/json" };
    if (draft.eTag) headers["if-match"] = draft.eTag;
    void run(
      "type",
      () =>
        fetch(`/api/v1/hr/letter-types/${encodeURIComponent(draft.key)}`, {
          method: "PUT",
          headers,
          body: JSON.stringify({
            names: { en: draft.nameEn, ar: draft.nameAr },
            templates: { en: draft.templateEn, ar: draft.templateAr },
            manualFields: draft.fields.map((f) => ({ key: f.key, names: { en: f.en, ar: f.ar } })),
            needsApproval: draft.needsApproval,
            orgUnitId: draft.unit || null,
            active: draft.active,
          }),
        }),
      async (response) => {
        const saved = (await response.json()) as LetterType;
        setTypes((all) => [...all.filter((x) => x.key !== saved.key), saved].sort((a, b) => a.key.localeCompare(b.key)));
        setDraft(toDraft(saved));
      },
    );
  }

  function saveSettings(enabled: boolean) {
    void run(
      "settings",
      () =>
        fetch("/api/v1/hr/letter-settings", {
          method: "PUT",
          headers: { "content-type": "application/json", "if-match": settings.eTag },
          body: JSON.stringify({ verificationEnabled: enabled }),
        }),
      async (response) => setSettings((await response.json()) as LetterSettings),
    );
  }

  return (
    <div className="letters">
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

      <section className="letters-section" aria-labelledby="letters-settings">
        <h2 id="letters-settings">{t("settings.title")}</h2>
        <Switch
          label={t("settings.verification")}
          hint={t("settings.verificationHint")}
          name="letters-verification"
          checked={settings.verificationEnabled}
          disabled={!settings.canChange || busy !== null}
          onChange={(e) => saveSettings(e.target.checked)}
        />
        {!settings.canChange ? <p className="field-hint">{t("settings.wholeOrganizationOnly")}</p> : null}
      </section>

      <section className="letters-section" aria-labelledby="letters-types">
        <h2 id="letters-types">{t("types.title")}</h2>
        <DataTable<LetterType>
          caption={t("types.caption")}
          captionHidden
          rows={types}
          rowKey={(r) => r.key}
          density="compact"
          emptyTitle={t("types.empty")}
          columns={[
            { key: "name", header: t("types.name"), rowHeader: true, sortValue: (r) => pick(r.names, locale, r.key), cell: (r) => pick(r.names, locale, r.key) },
            { key: "unit", header: t("types.unit"), cell: (r) => <bdi>{unitLabel(r.orgUnitId)}</bdi> },
            { key: "approval", header: t("types.approval"), cell: (r) => t(r.needsApproval ? "types.approvalYes" : "types.approvalNo") },
            { key: "active", header: t("types.state"), cell: (r) => <Badge tone={r.active ? "success" : "neutral"}>{t(r.active ? "types.active" : "types.inactive")}</Badge> },
            {
              key: "edit",
              header: t("types.actions"),
              cell: (r) =>
                r.canEdit ? (
                  <Button size="xs" onClick={() => setDraft(toDraft(r))}>
                    {t("types.edit")}
                  </Button>
                ) : null,
            },
          ]}
        />
      </section>

      <section className="letters-section" aria-labelledby="letters-editor">
        <h2 id="letters-editor">{draft.eTag ? t("editor.editTitle", { key: draft.key }) : t("editor.newTitle")}</h2>
        <p className="field-hint">{t("editor.placeholders", { list: LETTER_PLACEHOLDERS.map((p) => `{${p}}`).join(" ") })}</p>
        <div className="ui-control-row">
          <FormField label={t("editor.key")} name="type-key" value={draft.key} readOnly={draft.eTag !== null} maxLength={40} onChange={(e) => patch({ key: e.target.value })} />
          <FormField label={t("editor.nameEn")} name="type-name-en" value={draft.nameEn} maxLength={120} onChange={(e) => patch({ nameEn: e.target.value })} />
          <FormField label={t("editor.nameAr")} name="type-name-ar" dir="rtl" value={draft.nameAr} maxLength={120} onChange={(e) => patch({ nameAr: e.target.value })} />
          <SelectField
            label={t("types.unit")}
            name="type-unit"
            value={draft.unit}
            onChange={(e) => patch({ unit: e.target.value })}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitName(u, locale) }))]}
          />
        </div>
        <TextAreaField label={t("editor.templateEn")} name="type-template-en" rows={6} maxLength={5000} value={draft.templateEn} onChange={(e) => patch({ templateEn: e.target.value })} />
        <TextAreaField
          label={t("editor.templateAr")}
          name="type-template-ar"
          dir="rtl"
          rows={6}
          maxLength={5000}
          value={draft.templateAr}
          onChange={(e) => patch({ templateAr: e.target.value })}
        />
        <h3>{t("editor.fields")}</h3>
        <p className="field-hint">{t("editor.fieldsHint")}</p>
        {draft.fields.map((f, i) => (
          <div className="ui-control-row" key={i} role="group" aria-label={t("editor.field", { n: i + 1 })}>
            <FormField label={t("editor.fieldKey")} name={`field-key-${i}`} value={f.key} maxLength={40} onChange={(e) => patchField(i, { key: e.target.value })} />
            <FormField label={t("editor.nameEn")} name={`field-en-${i}`} value={f.en} maxLength={120} onChange={(e) => patchField(i, { en: e.target.value })} />
            <FormField label={t("editor.nameAr")} name={`field-ar-${i}`} dir="rtl" value={f.ar} maxLength={120} onChange={(e) => patchField(i, { ar: e.target.value })} />
            <Button size="sm" variant="quiet" onClick={() => patch({ fields: draft.fields.filter((_, j) => j !== i) })}>
              {t("editor.removeField")}
            </Button>
          </div>
        ))}
        {draft.fields.length < 10 ? (
          <div className="ui-control-row">
            <Button size="sm" icon="plus" onClick={() => patch({ fields: [...draft.fields, { key: "", en: "", ar: "" }], needsApproval: true })}>
              {t("editor.addField")}
            </Button>
          </div>
        ) : null}
        <Checkbox
          label={t("editor.needsApproval")}
          hint={t("editor.needsApprovalHint")}
          name="type-approval"
          checked={draft.needsApproval}
          onChange={(e) => patch({ needsApproval: e.target.checked })}
        />
        <Checkbox label={t("editor.active")} name="type-active" checked={draft.active} onChange={(e) => patch({ active: e.target.checked })} />
        <div className="ui-control-row">
          <Button variant="primary" busy={busy === "type"} busyLabel={t("saving")} disabled={busy !== null || !draft.key} onClick={saveType}>
            {t("editor.save")}
          </Button>
          {draft.eTag ? <Button onClick={() => setDraft(empty)}>{t("editor.new")}</Button> : null}
        </div>
      </section>

      <section className="letters-section" aria-labelledby="letters-issued">
        <h2 id="letters-issued">{t("letters.title")}</h2>
        <DataTable<Letter>
          caption={t("letters.caption")}
          captionHidden
          rows={letters}
          rowKey={(r) => r.id}
          density="compact"
          emptyTitle={t("letters.empty")}
          columns={[
            {
              key: "reference",
              header: tl("detail.reference"),
              rowHeader: true,
              sortValue: (r) => r.reference,
              cell: (r) => (
                <Link href={`/letters/${r.id}`}>
                  <bdi dir="ltr">{r.reference}</bdi>
                </Link>
              ),
            },
            { key: "person", header: tl("detail.person"), sortValue: (r) => r.person.displayName, cell: (r) => <bdi>{r.person.displayName}</bdi> },
            { key: "type", header: tl("type"), cell: (r) => pick(r.typeNames, locale, r.typeKey) },
            { key: "asked", header: t("letters.asked"), sortValue: (r) => r.createdAt, cell: (r) => format.dateTime(new Date(r.createdAt), { dateStyle: "medium" }) },
            { key: "status", header: tl("detail.status"), cell: (r) => <Badge tone={letterTone[r.status]}>{tl(`statuses.${r.status}`)}</Badge> },
          ]}
        />
      </section>
    </div>
  );
}
