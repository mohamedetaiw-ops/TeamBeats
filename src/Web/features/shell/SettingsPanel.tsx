"use client";

import { useId, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { PreferenceKey } from "@/lib/shell/types";
import { Icon } from "@/components/ui/Icon";
import { Popover } from "@/components/ui/Popover";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { Switch } from "@/components/ui/Switch";

const options: Record<PreferenceKey, string[]> = {
  mode: ["system", "light", "dark", "high_contrast"],
  density: ["comfortable", "compact"],
  motion: ["system", "reduced"],
  text_scale: ["100", "125", "150", "200"],
  focus: ["off", "on"],
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
  // The choice shows at once; the page renders again with the saved value when the server answers.
  const [chosen, setChosen] = useState<Partial<Record<PreferenceKey, string>>>({});
  const current = { ...values, ...chosen };
  const profile = (key: PreferenceKey, value: string) => {
    setChosen((c) => ({ ...c, [key]: value }));
    void save("/api/v1/me/preference-profile", { values: { [key]: value } });
  };
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
            value={current[key]}
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
        <select id={textId} value={current.text_scale} onChange={(e) => profile("text_scale", e.target.value)}>
          {options.text_scale.map((v) => (
            <option key={v} value={v}>
              {t(`text_scale.${v}`)}
            </option>
          ))}
        </select>
      </div>
      <Switch label={t("reduceMotion")} checked={current.motion === "reduced"} onChange={(e) => profile("motion", e.target.checked ? "reduced" : "system")} />
    </div>
  );
}

/**
 * Quick preferences in the shell bar (spec 036 DLS-047): the few display settings people change often, and a link to
 * every preference. Resetting personal layouts and connected apps such as Outlook live on the Preferences page, not here.
 */
export function SettingsPanel({ locales, locale, values }: { locales: string[]; locale: string; values: Record<PreferenceKey, string> }) {
  const t = useTranslations("shell.settings");
  // The controls render only while the panel is open, so the Preferences page keeps the only copy of each label.
  const [open, setOpen] = useState(false);
  return (
    <div className="shell-settings">
      <Popover
        label={t("open")}
        title={t("title")}
        icon="settings"
        iconOnly
        frame="panel"
        placement="bottom-end"
        testId="shell-settings-panel"
        onOpenChange={setOpen}
        footer={
          <Link href="/settings" className="ui-popover-more">
            {t("allPreferences")}
            <Icon name="arrow-forward" size={14} />
          </Link>
        }
      >
        {open ? <PreferenceControls locales={locales} locale={locale} values={values} /> : null}
      </Popover>
    </div>
  );
}
