"use client";

import { useId, useRef, useState, type KeyboardEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { PreferenceKey } from "@/lib/shell/types";
import { Icon } from "@/components/ui/Icon";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { Switch } from "@/components/ui/Switch";
import { useShellPanel } from "@/lib/shell/panel";

const options: Record<PreferenceKey, string[]> = {
  mode: ["system", "light", "dark"],
  density: ["comfortable", "compact"],
  motion: ["system", "reduced"],
  text_scale: ["100", "125", "150", "200"],
};

/** Saves one preference and renders the page again with it: language on the account (spec 007), the rest in the profile. */
function useSave() {
  const router = useRouter();
  const [saving, setSaving] = useState(false);
  async function save(url: string, body: unknown) {
    setSaving(true);
    try {
      await fetch(url, { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      router.refresh();
    } finally {
      setSaving(false);
    }
  }
  return { saving, save };
}

/**
 * The display preferences as compact controls (spec 008 FR-010, spec 036 DLS-047): appearance and density as segmented
 * controls, language and text size as a label with a small select, and reduce motion as a switch, because it takes
 * effect at once. The quick preferences popover and the Preferences page draw the same controls.
 */
export function PreferenceControls({ locales, locale, values }: { locales: string[]; locale: string; values: Record<PreferenceKey, string> }) {
  const t = useTranslations("shell.settings");
  const tl = useTranslations("locale");
  const { saving, save } = useSave();
  const profile = (key: PreferenceKey, value: string) => void save("/api/v1/me/preference-profile", { values: { [key]: value } });
  const languageId = useId();
  const textId = useId();
  return (
    <div className="shell-preferences" aria-busy={saving || undefined}>
      {(["mode", "density"] as const).map((key) => (
        <div key={key} className="shell-setting">
          <span className="shell-setting-label" aria-hidden="true">
            {t(`${key}.label`)}
          </span>
          <SegmentedControl
            label={t(`${key}.label`)}
            name={`pref-${key}`}
            value={values[key]}
            options={options[key].map((v) => ({ value: v, label: t(`${key}.${v}`) }))}
            onChange={(v) => profile(key, v)}
          />
        </div>
      ))}
      {locales.length > 1 ? (
        <div className="shell-setting-inline">
          <label htmlFor={languageId}>{t("language")}</label>
          <select id={languageId} value={locale} onChange={(e) => void save("/api/v1/me/preferences", { locale: e.target.value })}>
            {locales.map((l) => (
              <option key={l} value={l} lang={l}>
                {tl(l)}
              </option>
            ))}
          </select>
        </div>
      ) : null}
      <div className="shell-setting-inline">
        <label htmlFor={textId}>{t("text_scale.label")}</label>
        <select id={textId} value={values.text_scale} onChange={(e) => profile("text_scale", e.target.value)}>
          {options.text_scale.map((v) => (
            <option key={v} value={v}>
              {t(`text_scale.${v}`)}
            </option>
          ))}
        </select>
      </div>
      <Switch label={t("reduceMotion")} checked={values.motion === "reduced"} onChange={(e) => profile("motion", e.target.checked ? "reduced" : "system")} />
    </div>
  );
}

/**
 * Quick preferences in the shell bar (spec 036 DLS-047): the few display settings people change often, and a link to
 * every preference. Resetting personal layouts and connected apps such as Outlook live on the Preferences page, not here.
 */
export function SettingsPanel({ locales, locale, values }: { locales: string[]; locale: string; values: Record<PreferenceKey, string> }) {
  const t = useTranslations("shell.settings");
  const root = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useShellPanel(root);
  const panelId = useId();
  const button = useRef<HTMLButtonElement>(null);

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && open) {
      setOpen(false);
      button.current?.focus();
    }
  }

  return (
    <div ref={root} className="shell-settings" onKeyDown={onKeyDown}>
      <button ref={button} type="button" className="button button-quiet shell-icon-button" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen(!open)}>
        <Icon name="settings" size={20} />
        <span className="visually-hidden">{t("open")}</span>
      </button>
      <div className="shell-settings-panel shell-popover" id={panelId} role="dialog" aria-labelledby={`${panelId}-title`} hidden={!open}>
        <div className="shell-popover-head">
          <h2 className="shell-popover-title" id={`${panelId}-title`}>
            {t("title")}
          </h2>
        </div>
        <div className="shell-popover-body">{open ? <PreferenceControls locales={locales} locale={locale} values={values} /> : null}</div>
        <div className="shell-popover-foot">
          <Link href="/settings" className="shell-popover-anchor shell-popover-more">
            {t("allPreferences")}
            <Icon name="arrow-forward" size={14} />
          </Link>
        </div>
      </div>
    </div>
  );
}
