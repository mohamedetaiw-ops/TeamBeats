"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages, type NotificationSettings as Settings } from "@/lib/notifications/types";
import "./notifications.css";

type Relay = { host: string; port: string; username: string; secretReference: string; startTls: boolean };

/**
 * Tenant notification settings (spec 015 US4): sender name, default email per category, categories the tenant makes
 * mandatory, and its own SMTP relay when the plan includes one. The API authorizes every call and checks the plan;
 * the relay password is never entered here, only a reference to the secret manager.
 */
export function NotificationSettings({ initial }: { initial: Settings }) {
  const t = useTranslations("notifications.settings");
  const tc = useTranslations("notifications.category");
  const [settings, setSettings] = useState(initial);
  const [sender, setSender] = useState(initial.senderName ?? "");
  const [useRelay, setUseRelay] = useState(initial.relay !== null);
  const [relay, setRelay] = useState<Relay>({
    host: initial.relay?.host ?? "",
    port: String(initial.relay?.port ?? 587),
    username: initial.relay?.username ?? "",
    secretReference: initial.relay?.secretReference ?? "",
    startTls: initial.relay?.startTls ?? true,
  });
  const [defaults, setDefaults] = useState(initial.defaultEmail);
  const [extra, setExtra] = useState(new Set(initial.extraMandatory));
  const [busy, setBusy] = useState<"save" | "test" | null>(null);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const optional = Object.keys(settings.defaultEmail);

  async function save() {
    setBusy("save");
    setResult(null);
    try {
      const response = await fetch("/api/v1/notifications/settings", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          senderName: sender.trim() || null,
          relay: useRelay
            ? {
                host: relay.host,
                port: Number(relay.port),
                username: relay.username.trim() || null,
                secretReference: relay.secretReference.trim() || null,
                startTls: relay.startTls,
              }
            : null,
          defaultEmail: defaults,
          extraMandatory: [...extra],
          version: settings.version,
        }),
      });
      if (response.ok) {
        setSettings((await response.json()) as Settings);
        setResult({ ok: true, messages: [t("saved")] });
      } else {
        setResult({ ok: false, messages: response.status === 409 ? [t("stale")] : await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  async function test() {
    setBusy("test");
    setResult(null);
    try {
      const response = await fetch("/api/v1/notifications/settings/test", { method: "POST" });
      if (response.ok) {
        const answer = (await response.json()) as { outcome: "sent" | "refused" | "failed"; error: string | null };
        setResult({ ok: answer.outcome === "sent", messages: [t(`test.${answer.outcome}`), ...(answer.error ? [answer.error] : [])] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  const field = (key: keyof Omit<Relay, "startTls">, props: { type?: string; hint?: string; dir?: string } = {}) => (
    <FormField
      label={t(`relay.${key}`)}
      name={`relay-${key}`}
      value={relay[key]}
      onChange={(e) => setRelay((r) => ({ ...r, [key]: e.target.value }))}
      {...props}
    />
  );

  return (
    <form
      className="notification-preferences"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <FormField label={t("senderName")} hint={t("senderNameHint")} name="senderName" value={sender} maxLength={80} onChange={(e) => setSender(e.target.value)} />

      <div className="notification-table-wrap">
        <table className="notification-table">
          <caption>{t("categoriesCaption")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("category")}</th>
              <th scope="col">{t("emailDefault")}</th>
              <th scope="col">{t("mandatory")}</th>
            </tr>
          </thead>
          <tbody>
            {settings.platformMandatory.map((c) => (
              <tr key={c}>
                <th scope="row">{tc(c)}</th>
                <td>{t("always")}</td>
                <td>{t("platformMandatory")}</td>
              </tr>
            ))}
            {optional.map((c) => (
              <tr key={c} data-category={c}>
                <th scope="row">{tc(c)}</th>
                <td>
                  <input
                    type="checkbox"
                    aria-label={t("emailDefaultFor", { category: tc(c) })}
                    checked={extra.has(c) || (defaults[c] ?? false)}
                    disabled={extra.has(c)}
                    onChange={(e) => setDefaults((d) => ({ ...d, [c]: e.target.checked }))}
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    aria-label={t("mandatoryFor", { category: tc(c) })}
                    checked={extra.has(c)}
                    onChange={(e) =>
                      setExtra((x) => {
                        const next = new Set(x);
                        if (e.target.checked) next.add(c);
                        else next.delete(c);
                        return next;
                      })
                    }
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <fieldset className="notification-fieldset">
        <legend>{t("relay.title")}</legend>
        {settings.tenantRelayInPlan ? (
          <>
            <label className="notification-check">
              <input type="checkbox" checked={useRelay} onChange={(e) => setUseRelay(e.target.checked)} />
              <span>{t("relay.use")}</span>
            </label>
            {useRelay ? (
              <>
                <div className="notification-row">
                  {field("host", { dir: "ltr" })}
                  {field("port", { type: "number", dir: "ltr" })}
                </div>
                <div className="notification-row">
                  {field("username", { dir: "ltr" })}
                  {field("secretReference", { hint: t("relay.secretReferenceHint"), dir: "ltr" })}
                </div>
                <label className="notification-check">
                  <input type="checkbox" checked={relay.startTls} onChange={(e) => setRelay((r) => ({ ...r, startTls: e.target.checked }))} />
                  <span>{t("relay.startTls")}</span>
                </label>
              </>
            ) : (
              <p>{t("relay.platform")}</p>
            )}
          </>
        ) : (
          <p>{t("relay.notInPlan")}</p>
        )}
      </fieldset>

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="notification-actions">
        <Button type="submit" variant="primary" busy={busy === "save"} busyLabel={t("saving")}>
          {t("save")}
        </Button>
        <Button variant="secondary" busy={busy === "test"} busyLabel={t("testing")} onClick={() => void test()}>
          {t("test.action")}
        </Button>
      </div>
    </form>
  );
}

