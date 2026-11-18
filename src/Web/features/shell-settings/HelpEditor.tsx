"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import "./shell-settings.css";

type Labels = Record<string, string>;
type StepRow = { id: string; titleEn: string; titleAr: string; bodyEn: string; bodyAr: string };
type ItemRow = { id: string; key: string; kind: "hint" | "tour"; path: string; titleEn: string; titleAr: string; bodyEn: string; bodyAr: string; steps: StepRow[] };

export type HelpAdmin = {
  version: number;
  definition: { items: { key: string; kind: "hint" | "tour"; path: string; title: Labels | null; body: Labels | null; steps: { title: Labels | null; body: Labels | null }[] | null }[] | null };
};

const newId = () => crypto.randomUUID();
const emptyStep = (): StepRow => ({ id: newId(), titleEn: "", titleAr: "", bodyEn: "", bodyAr: "" });

/** Texts with only the languages that were filled in; null when none were. */
function labels(en: string, ar: string): Labels | null {
  const result: Labels = {};
  if (en.trim()) result.en = en.trim();
  if (ar.trim()) result.ar = ar.trim();
  return Object.keys(result).length > 0 ? result : null;
}

/**
 * Help hints and short tours (spec 008 FR-020, T024): people with help.edit write a hint or a tour for an address in
 * English and Arabic and publish them as one new version. The API checks the schema (keys, addresses, lengths, at most
 * eight steps) and keeps every earlier version; this form only collects the text.
 */
export function HelpEditor({ initial }: { initial: HelpAdmin }) {
  const t = useTranslations("help_settings");
  const router = useRouter();
  const [items, setItems] = useState<ItemRow[]>(() =>
    (initial.definition.items ?? []).map((i) => ({
      id: newId(),
      key: i.key,
      kind: i.kind,
      path: i.path,
      titleEn: i.title?.en ?? "",
      titleAr: i.title?.ar ?? "",
      bodyEn: i.body?.en ?? "",
      bodyAr: i.body?.ar ?? "",
      steps: (i.steps ?? []).map((s) => ({ id: newId(), titleEn: s.title?.en ?? "", titleAr: s.title?.ar ?? "", bodyEn: s.body?.en ?? "", bodyAr: s.body?.ar ?? "" })),
    })),
  );
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);

  const update = (id: string, patch: Partial<ItemRow>) => setItems((list) => list.map((i) => (i.id === id ? { ...i, ...patch } : i)));
  const updateStep = (item: ItemRow, id: string, patch: Partial<StepRow>) => update(item.id, { steps: item.steps.map((s) => (s.id === id ? { ...s, ...patch } : s)) });

  async function publish(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setResult(null);
    const body = {
      items: items.map((i) => ({
        key: i.key.trim(),
        kind: i.kind,
        path: i.path.trim(),
        title: labels(i.titleEn, i.titleAr),
        body: i.kind === "hint" ? labels(i.bodyEn, i.bodyAr) : null,
        steps: i.kind === "tour" ? i.steps.map((s) => ({ title: labels(s.titleEn, s.titleAr), body: labels(s.bodyEn, s.bodyAr) })) : null,
      })),
    };
    try {
      const response = await fetch("/api/v1/experience/help", { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (response.ok) {
        const view = (await response.json()) as HelpAdmin;
        setResult({ ok: true, messages: [t("published", { version: view.version })] });
        router.refresh();
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="shell-settings" onSubmit={(e) => void publish(e)}>
      <p className="shell-settings-hint">{t("version", { version: initial.version })}</p>
      {items.length === 0 ? <p className="shell-settings-hint">{t("empty")}</p> : null}
      {items.map((item, index) => (
        <fieldset key={item.id} className="shell-settings-section">
          <legend>{t("item", { number: index + 1 })}</legend>
          <div className="shell-settings-grid">
            <FormField label={t("key")} name={`key-${item.id}`} hint={t("keyHint")} value={item.key} required maxLength={60} dir="ltr" onChange={(e) => update(item.id, { key: e.target.value })} />
            <SelectField
              label={t("kind")}
              name={`kind-${item.id}`}
              value={item.kind}
              options={[
                { value: "hint", label: t("kinds.hint") },
                { value: "tour", label: t("kinds.tour") },
              ]}
              onChange={(e) => update(item.id, { kind: e.target.value as ItemRow["kind"], steps: item.steps.length > 0 ? item.steps : [emptyStep()] })}
            />
            <FormField label={t("path")} name={`path-${item.id}`} hint={t("pathHint")} value={item.path} required maxLength={300} dir="ltr" onChange={(e) => update(item.id, { path: e.target.value })} />
          </div>
          <div className="shell-settings-grid">
            <FormField label={t("titleEn")} name={`title-en-${item.id}`} value={item.titleEn} maxLength={120} lang="en" dir="ltr" onChange={(e) => update(item.id, { titleEn: e.target.value })} />
            <FormField label={t("titleAr")} name={`title-ar-${item.id}`} value={item.titleAr} maxLength={120} lang="ar" dir="rtl" onChange={(e) => update(item.id, { titleAr: e.target.value })} />
          </div>
          {item.kind === "hint" ? (
            <div className="shell-settings-grid">
              <TextAreaField label={t("bodyEn")} name={`body-en-${item.id}`} value={item.bodyEn} maxLength={600} lang="en" dir="ltr" onChange={(e) => update(item.id, { bodyEn: e.target.value })} />
              <TextAreaField label={t("bodyAr")} name={`body-ar-${item.id}`} value={item.bodyAr} maxLength={600} lang="ar" dir="rtl" onChange={(e) => update(item.id, { bodyAr: e.target.value })} />
            </div>
          ) : (
            <>
              {item.steps.map((step, s) => (
                <fieldset key={step.id} className="shell-settings-section">
                  <legend>{t("stepLegend", { number: s + 1 })}</legend>
                  <div className="shell-settings-grid">
                    <FormField label={t("titleEn")} name={`step-title-en-${step.id}`} value={step.titleEn} maxLength={120} lang="en" dir="ltr" onChange={(e) => updateStep(item, step.id, { titleEn: e.target.value })} />
                    <FormField label={t("titleAr")} name={`step-title-ar-${step.id}`} value={step.titleAr} maxLength={120} lang="ar" dir="rtl" onChange={(e) => updateStep(item, step.id, { titleAr: e.target.value })} />
                  </div>
                  <div className="shell-settings-grid">
                    <TextAreaField label={t("bodyEn")} name={`step-en-${step.id}`} value={step.bodyEn} maxLength={600} lang="en" dir="ltr" onChange={(e) => updateStep(item, step.id, { bodyEn: e.target.value })} />
                    <TextAreaField label={t("bodyAr")} name={`step-ar-${step.id}`} value={step.bodyAr} maxLength={600} lang="ar" dir="rtl" onChange={(e) => updateStep(item, step.id, { bodyAr: e.target.value })} />
                  </div>
                  <Button variant="quiet" icon="trash" disabled={item.steps.length <= 1} onClick={() => update(item.id, { steps: item.steps.filter((x) => x.id !== step.id) })}>
                    {t("removeStep", { number: s + 1 })}
                  </Button>
                </fieldset>
              ))}
              <Button variant="secondary" icon="plus" disabled={item.steps.length >= 8} onClick={() => update(item.id, { steps: [...item.steps, emptyStep()] })}>
                {t("addStep")}
              </Button>
            </>
          )}
          <Button variant="quiet" icon="trash" onClick={() => setItems((list) => list.filter((x) => x.id !== item.id))}>
            {t("remove", { number: index + 1 })}
          </Button>
        </fieldset>
      ))}
      <div className="shell-settings-actions">
        <Button
          variant="secondary"
          icon="plus"
          disabled={items.length >= 60}
          onClick={() => setItems((list) => [...list, { id: newId(), key: "", kind: "hint", path: "/", titleEn: "", titleAr: "", bodyEn: "", bodyAr: "", steps: [] }])}
        >
          {t("addHint")}
        </Button>
        <Button
          variant="secondary"
          icon="plus"
          disabled={items.length >= 60}
          onClick={() => setItems((list) => [...list, { id: newId(), key: "", kind: "tour", path: "/", titleEn: "", titleAr: "", bodyEn: "", bodyAr: "", steps: [emptyStep()] }])}
        >
          {t("addTour")}
        </Button>
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("publishing")}>
          {t("publish")}
        </Button>
      </div>
      {result ? (
        <Alert tone={result.ok ? "success" : "danger"} title={result.ok ? result.messages[0]! : t("refused")} live>
          {result.ok ? null : (
            <ul>
              {result.messages.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          )}
        </Alert>
      ) : null}
    </form>
  );
}
