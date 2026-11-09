"use client";

import { useId, useRef, useState, type KeyboardEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { PreferenceKey } from "@/lib/shell/types";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { Icon } from "@/components/ui/Icon";
import { OutlookSettings } from "./OutlookSettings";

const options: Record<PreferenceKey, string[]> = {
  mode: ["system", "light", "dark"],
  density: ["comfortable", "compact"],
  motion: ["system", "reduced"],
  text_scale: ["100", "125", "150", "200"],
};

/**
 * One place on every page for language, mode, density, motion and text size (spec 008 FR-010). Language is saved on
 * the user account (spec 007), the rest in the preference profile; the page re-renders with the new values. Resetting
 * every personal layout lives here too, and keeps language and the other preferences (FR-027). The Outlook connection
 * (spec 018 FR-013) is read when the panel opens.
 */
export function SettingsPanel({ locales, locale, values }: { locales: string[]; locale: string; values: Record<PreferenceKey, string> }) {
  const t = useTranslations("shell.settings");
  const tl = useTranslations("locale");
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const panelId = useId();
  const button = useRef<HTMLButtonElement>(null);

  async function save(url: string, body: unknown) {
    setSaving(true);
    try {
      await fetch(url, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      router.refresh();
    } finally {
      setSaving(false);
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && open) {
      setOpen(false);
      button.current?.focus();
    }
  }

  return (
    <div className="shell-settings" onKeyDown={onKeyDown}>
      <button ref={button} type="button" className="button button-quiet shell-icon-button" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen((x) => !x)}>
        <Icon name="settings" size={20} />
        <span className="visually-hidden">{t("open")}</span>
      </button>
      <div className="shell-settings-panel" id={panelId} hidden={!open} aria-busy={saving || undefined}>
        {locales.length > 1 ? (
          <label className="shell-setting">
            <span>{t("language")}</span>
            <select value={locale} onChange={(e) => void save("/api/v1/me/preferences", { locale: e.target.value })}>
              {locales.map((l) => (
                <option key={l} value={l} lang={l}>
                  {tl(l)}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        {(Object.keys(options) as PreferenceKey[]).map((key) => (
          <label key={key} className="shell-setting">
            <span>{t(`${key}.label`)}</span>
            <select value={values[key]} onChange={(e) => void save("/api/v1/me/preference-profile", { values: { [key]: e.target.value } })}>
              {options[key].map((v) => (
                <option key={v} value={v}>
                  {t(`${key}.${v}`)}
                </option>
              ))}
            </select>
          </label>
        ))}
        <ConfirmAction
          label={t("resetLayouts")}
          question={t("resetLayoutsQuestion")}
          confirmLabel={t("resetLayoutsConfirm")}
          cancelLabel={t("resetLayoutsCancel")}
          onConfirm={async () => {
            await fetch("/api/v1/me/layouts", { method: "DELETE" });
            router.refresh();
          }}
        />
        {open ? <OutlookSettings /> : null}
      </div>
    </div>
  );
}
