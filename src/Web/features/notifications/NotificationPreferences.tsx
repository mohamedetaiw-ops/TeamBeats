"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages, type Preferences } from "@/lib/notifications/types";
import "./notifications.css";

/**
 * An employee's notification preferences (spec 015 US2, US3): email and digest per category, quiet hours, digest time
 * and time zone. In-app is always on. Mandatory categories show as fixed; the API ignores any change to them anyway.
 */
export function NotificationPreferences({ initial }: { initial: Preferences }) {
  const t = useTranslations("notifications.preferences");
  const tc = useTranslations("notifications.category");
  const [prefs, setPrefs] = useState(initial);
  const [quiet, setQuiet] = useState(initial.quietHours !== null);
  const [start, setStart] = useState(initial.quietHours?.start ?? "22:00");
  const [end, setEnd] = useState(initial.quietHours?.end ?? "07:00");
  const [saving, setSaving] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);

  function setCategory(category: string, change: { email?: boolean; digest?: boolean }) {
    setPrefs((p) => ({ ...p, categories: p.categories.map((c) => (c.category === category ? { ...c, ...change } : c)) }));
  }

  async function save() {
    setSaving(true);
    setResult(null);
    try {
      const response = await fetch("/api/v1/me/notification-preferences", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          categories: prefs.categories.filter((c) => !c.mandatory).map((c) => ({ category: c.category, email: c.email, digest: c.email && c.digest })),
          quietHours: quiet ? { start, end } : null,
          digestTime: prefs.digestTime,
          timeZone: prefs.timeZone,
        }),
      });
      if (response.ok) {
        setPrefs((await response.json()) as Preferences);
        setResult({ ok: true, messages: [t("saved")] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setSaving(false);
    }
  }

  return (
    <form
      className="notification-preferences"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      {prefs.emailPaused ? (
        <p className="notice notification-paused" role="status">
          {t("emailPaused")}
        </p>
      ) : null}
      <p>{t("inAppAlways")}</p>
      <div className="notification-table-wrap">
        <table className="notification-table">
          <caption className="visually-hidden">{t("tableCaption")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("category")}</th>
              <th scope="col">{t("email")}</th>
              <th scope="col">{t("digest")}</th>
            </tr>
          </thead>
          <tbody>
            {prefs.categories.map((c) => (
              <tr key={c.category} data-category={c.category}>
                <th scope="row">
                  {tc(c.category)}
                  {c.mandatory ? <span className="notification-tag">{t("mandatory")}</span> : null}
                </th>
                <td>
                  <input
                    type="checkbox"
                    aria-label={t("emailFor", { category: tc(c.category) })}
                    checked={c.email}
                    disabled={c.mandatory}
                    onChange={(e) => setCategory(c.category, { email: e.target.checked })}
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    aria-label={t("digestFor", { category: tc(c.category) })}
                    checked={c.email && c.digest}
                    disabled={c.mandatory || !c.email}
                    onChange={(e) => setCategory(c.category, { digest: e.target.checked })}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <fieldset className="notification-fieldset">
        <legend>{t("quietHours")}</legend>
        <p className="field-hint">{t("quietHoursHint")}</p>
        <label className="notification-check">
          <input type="checkbox" checked={quiet} onChange={(e) => setQuiet(e.target.checked)} />
          <span>{t("quietHoursOn")}</span>
        </label>
        {quiet ? (
          <div className="notification-row">
            <FormField label={t("quietStart")} name="quietStart" type="time" value={start} onChange={(e) => setStart(e.target.value)} />
            <FormField label={t("quietEnd")} name="quietEnd" type="time" value={end} onChange={(e) => setEnd(e.target.value)} />
          </div>
        ) : null}
      </fieldset>
      <div className="notification-row">
        <FormField
          label={t("digestTime")}
          name="digestTime"
          type="time"
          value={prefs.digestTime}
          onChange={(e) => setPrefs((p) => ({ ...p, digestTime: e.target.value }))}
        />
        <FormField
          label={t("timeZone")}
          hint={t("timeZoneHint")}
          name="timeZone"
          dir="ltr"
          value={prefs.timeZone}
          onChange={(e) => setPrefs((p) => ({ ...p, timeZone: e.target.value }))}
        />
      </div>
      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <Button type="submit" variant="primary" busy={saving} busyLabel={t("saving")}>
        {t("save")}
      </Button>
    </form>
  );
}
