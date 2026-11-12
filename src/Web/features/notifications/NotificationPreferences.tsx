"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages, type Preferences } from "@/lib/notifications/types";
import { deviceSubscription, pushSupported, turnOffPush, turnOnPush } from "@/lib/push";
import "./notifications.css";

/**
 * An employee's notification preferences (spec 015 US2, US3, US8): email, digest and Slack per category, quiet hours,
 * digest time and time zone. In-app is always on. Slack shows only when the organization connected it, and works once
 * the person linked their Slack account. Push (spec 030 US2) shows when the plan includes it: the person turns it on for
 * this device and picks the categories; a device that cannot receive push says so and points to email. Mandatory
 * categories show as fixed; the API ignores any change to them anyway.
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
  const [linking, setLinking] = useState(false);
  const slack = prefs.slack.available;
  const push = prefs.push.available && prefs.push.publicKey !== null;
  // What this browser allows is only known after hydration: "unknown" renders nothing until then.
  const [device, setDevice] = useState<"unknown" | "unsupported" | "blocked" | "off" | "on">("unknown");
  const [switching, setSwitching] = useState(false);

  useEffect(() => {
    if (!push) return;
    let live = true;
    const look = async (): Promise<typeof device> => {
      if (!pushSupported()) return "unsupported";
      if (Notification.permission === "denied") return "blocked";
      const subscription = await deviceSubscription().catch(() => null);
      return subscription && Notification.permission === "granted" ? "on" : "off";
    };
    void look().then((state) => live && setDevice(state));
    return () => {
      live = false;
    };
  }, [push]);

  async function switchDevice(on: boolean) {
    setSwitching(true);
    setResult(null);
    try {
      if (on) {
        const outcome = await turnOnPush(prefs.push.publicKey!);
        if (outcome === "on") {
          setDevice("on");
          setPrefs((p) => ({ ...p, push: { ...p.push, devices: p.push.devices + 1 } }));
          setResult({ ok: true, messages: [t("pushTurnedOn")] });
        } else if (outcome === "denied") {
          setDevice(Notification.permission === "denied" ? "blocked" : "off");
          setResult({ ok: false, messages: [t("pushDenied")] });
        } else if (outcome === "unsupported") {
          setDevice("unsupported");
        } else {
          setResult({ ok: false, messages: [t("failed")] });
        }
      } else if (await turnOffPush()) {
        setDevice("off");
        setResult({ ok: true, messages: [t("pushTurnedOff")] });
      } else {
        setResult({ ok: false, messages: [t("failed")] });
      }
    } finally {
      setSwitching(false);
    }
  }

  async function link(on: boolean) {
    setLinking(true);
    setResult(null);
    try {
      const response = await fetch(on ? "/api/v1/me/notification-preferences/slack:link" : "/api/v1/me/notification-preferences/slack", { method: on ? "POST" : "DELETE" });
      if (response.ok) {
        setPrefs((await response.json()) as Preferences);
        setResult({ ok: true, messages: [t(on ? "slackLinked" : "slackUnlinked")] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setLinking(false);
    }
  }

  function setCategory(category: string, change: { email?: boolean; digest?: boolean; slack?: boolean; push?: boolean }) {
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
          categories: prefs.categories
            .filter((c) => !c.mandatory)
            .map((c) => ({ category: c.category, email: c.email, digest: c.email && c.digest, ...(slack ? { slack: c.slack } : {}), ...(push ? { push: c.push } : {}) })),
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
      {slack ? (
        <div className="notification-row">
          <p>{t(prefs.slack.linked ? "slackIsLinked" : "slackNotLinked")}</p>
          <Button busy={linking} busyLabel={t("saving")} onClick={() => void link(!prefs.slack.linked)}>
            {t(prefs.slack.linked ? "slackUnlink" : "slackLink")}
          </Button>
        </div>
      ) : null}
      {push && device !== "unknown" ? (
        <div className="notification-row" data-push={device}>
          {device === "unsupported" ? (
            <p className="notice">{t("pushUnsupported")}</p>
          ) : device === "blocked" ? (
            <p className="notice">{t("pushBlocked")}</p>
          ) : (
            <>
              <p>{t(device === "on" ? "pushIsOn" : "pushIsOff")}</p>
              <Button busy={switching} busyLabel={t("saving")} onClick={() => void switchDevice(device !== "on")}>
                {t(device === "on" ? "pushTurnOff" : "pushTurnOn")}
              </Button>
            </>
          )}
        </div>
      ) : null}
      <div className="notification-table-wrap">
        <table className="notification-table">
          <caption className="visually-hidden">{t("tableCaption")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("category")}</th>
              <th scope="col">{t("email")}</th>
              <th scope="col">{t("digest")}</th>
              {slack ? <th scope="col">{t("slack")}</th> : null}
              {push ? <th scope="col">{t("push")}</th> : null}
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
                {slack ? (
                  <td>
                    <input
                      type="checkbox"
                      aria-label={t("slackFor", { category: tc(c.category) })}
                      checked={prefs.slack.linked && c.slack}
                      disabled={c.mandatory || !prefs.slack.linked}
                      onChange={(e) => setCategory(c.category, { slack: e.target.checked })}
                    />
                  </td>
                ) : null}
                {push ? (
                  <td>
                    <input
                      type="checkbox"
                      aria-label={t("pushFor", { category: tc(c.category) })}
                      checked={c.push}
                      disabled={c.mandatory}
                      onChange={(e) => setCategory(c.category, { push: e.target.checked })}
                    />
                  </td>
                ) : null}
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
