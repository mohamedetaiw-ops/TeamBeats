"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import type { ContentSettingsView, ContentTypeSettings } from "@/lib/content/types";
import "./content.css";

type Draft = { months: string; action: "" | "versions" | "item" };

/**
 * Retention per content type (spec 009 FR-020): after a number of months, delete old versions (never the live or draft
 * one) or archived items. Nothing is deleted while it is off, and the API never deletes what a legal hold keeps.
 */
export function RetentionSettings({ settings }: { settings: ContentSettingsView }) {
  const t = useTranslations("content.health.retention");
  const types = useTranslations("content.health.types");
  const router = useRouter();
  const [drafts, setDrafts] = useState<Record<string, Draft>>(() =>
    Object.fromEntries(settings.types.map((s) => [s.key, { months: s.retentionMonths ? String(s.retentionMonths) : "", action: s.retentionAction ?? "" }])),
  );
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setSaved(false);
    setMessages([]);
    const next: ContentTypeSettings[] = settings.types.map((s) => {
      const d = drafts[s.key];
      const on = d && d.action !== "";
      return { ...s, retentionMonths: on ? Number(d.months) || null : null, retentionAction: on ? d.action || null : null };
    });
    try {
      const response = await fetch("/api/v1/content/settings", { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ types: next }) });
      if (response.ok) {
        setSaved(true);
        router.refresh();
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="content-card" aria-labelledby="content-retention">
      <h2 id="content-retention">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="notice notice-success" role="status">
          {t("saved")}
        </p>
      ) : null}
      <form className="content-site-form" onSubmit={(e) => void save(e)}>
        {settings.types.map((s) => {
          const d = drafts[s.key] ?? { months: "", action: "" };
          const label = types.has(s.key) ? types(s.key) : s.key;
          return (
            <fieldset key={s.key} className="content-route">
              <legend>{label}</legend>
              <SelectField
                label={t("action")}
                name={`action-${s.key}`}
                value={d.action}
                onChange={(e) => setDrafts({ ...drafts, [s.key]: { ...d, action: e.target.value as Draft["action"] } })}
                options={[
                  { value: "", label: t("keep") },
                  { value: "versions", label: t("versions") },
                  { value: "item", label: t("item") },
                ]}
              />
              {d.action ? (
                <FormField
                  label={t("months")}
                  name={`months-${s.key}`}
                  type="number"
                  min={1}
                  max={120}
                  required
                  value={d.months}
                  onChange={(e) => setDrafts({ ...drafts, [s.key]: { ...d, months: e.target.value } })}
                />
              ) : null}
            </fieldset>
          );
        })}
        <p className="content-actions">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
            {t("save")}
          </Button>
        </p>
      </form>
    </section>
  );
}
