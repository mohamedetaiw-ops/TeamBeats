"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { ResultAlert, useAdminAction, useWhen } from "./shared";

export type CustomDomain = {
  id: string;
  host: string;
  state: "pending" | "verified" | "failed";
  recordName: string;
  recordValue: string;
  appTitle: Record<string, string>;
  description: Record<string, string>;
  signInMessage: Record<string, string>;
  verifiedAt: string | null;
  checkedAt: string | null;
  failure: string | null;
  certificateExpiresAt: string | null;
};

type Texts = { appTitle: Record<string, string>; description: Record<string, string>; signInMessage: Record<string, string> };

const TONES = { pending: "warning", verified: "success", failed: "danger" } as const;

/**
 * Custom domains (spec 033 R2 T021, US7, FR-010): add a host, publish the TXT record shown, verify, then set what the
 * domain shows in each language. The API decides what is allowed and whether a host is served; this screen only shows
 * the API's answer.
 */
export function DomainsAdmin({ initial, locales }: { initial: CustomDomain[]; locales: string[] }) {
  const t = useTranslations("admin.tenant.domains");
  const tl = useTranslations("admin.org.starter.languages");
  const when = useWhen();
  const { busy, result, run } = useAdminAction("/admin/tenant/domains");
  const [domains, setDomains] = useState(initial);
  const [host, setHost] = useState("");
  const [texts, setTexts] = useState<Record<string, Texts>>(() =>
    Object.fromEntries(initial.map((d) => [d.id, { appTitle: d.appTitle, description: d.description, signInMessage: d.signInMessage }])),
  );

  const language = (l: string) => (tl.has(l as never) ? tl(l as never) : l);
  const replace = (view: CustomDomain) => setDomains((list) => list.map((d) => (d.id === view.id ? view : d)));

  async function add() {
    const outcome = await run<CustomDomain>("add", "POST", "/api/v1/tenant/domains", { host }, t("added"));
    if (!outcome.ok) return;
    setDomains((list) => [...list, outcome.data]);
    setTexts((all) => ({ ...all, [outcome.data.id]: { appTitle: {}, description: {}, signInMessage: {} } }));
    setHost("");
  }

  async function verify(d: CustomDomain) {
    const outcome = await run<CustomDomain>(`verify-${d.id}`, "POST", `/api/v1/tenant/domains/${d.id}/verify`, undefined, (v) =>
      v.state === "verified" ? t("verifiedDone", { host: d.host }) : t(`failures.${v.failure ?? "record_missing"}`),
    );
    if (outcome.ok) replace(outcome.data);
  }

  async function save(d: CustomDomain) {
    const outcome = await run<CustomDomain>(`save-${d.id}`, "PUT", `/api/v1/tenant/domains/${d.id}/presentation`, texts[d.id], t("saved"));
    if (outcome.ok) replace(outcome.data);
  }

  async function remove(d: CustomDomain) {
    const outcome = await run(`remove-${d.id}`, "DELETE", `/api/v1/tenant/domains/${d.id}`, undefined, t("removed", { host: d.host }));
    if (outcome.ok) setDomains((list) => list.filter((x) => x.id !== d.id));
  }

  function setText(id: string, field: keyof Texts, locale: string, value: string) {
    setTexts((all) => ({ ...all, [id]: { ...all[id]!, [field]: { ...all[id]![field], [locale]: value } } }));
  }

  return (
    <div className="admin-screen">
      <ResultAlert result={result} />
      <section className="admin-panel" aria-labelledby="domain-add-title">
        <div className="admin-panel-head">
          <h2 id="domain-add-title">{t("addTitle")}</h2>
        </div>
        <p className="admin-meta">{t("intro")}</p>
        <FormField label={t("host")} hint={t("hostHint")} name="host" dir="ltr" value={host} onChange={(e) => setHost(e.target.value)} />
        <div className="ui-control-row">
          <Button variant="primary" disabled={busy !== null || host.trim() === ""} onClick={() => void add()}>
            {t("add")}
          </Button>
        </div>
      </section>

      {domains.length === 0 ? <StateView kind="empty" title={t("emptyTitle")} message={t("emptyMessage")} /> : null}

      {domains.map((d) => (
        <section key={d.id} className="admin-panel" aria-labelledby={`domain-${d.id}`} data-testid="custom-domain">
          <div className="admin-panel-head">
            <h2 id={`domain-${d.id}`}>
              <bdi dir="ltr">{d.host}</bdi>
            </h2>
            <Badge tone={TONES[d.state]}>{t(`states.${d.state}`)}</Badge>
          </div>
          {d.state === "verified" ? (
            <p className="admin-meta">
              {t("servedSince", { when: when(d.verifiedAt) })}
              {d.certificateExpiresAt ? ` ${t("certificateUntil", { when: when(d.certificateExpiresAt) })}` : ""}
            </p>
          ) : (
            <>
              <p className="admin-meta">{d.failure ? t(`failures.${d.failure}`) : t("recordIntro")}</p>
              <div className="admin-fields">
                <FormField label={t("recordName")} name={`record-name-${d.id}`} dir="ltr" readOnly value={d.recordName} />
                <FormField label={t("recordValue")} name={`record-value-${d.id}`} dir="ltr" readOnly value={d.recordValue} />
              </div>
            </>
          )}

          <fieldset className="admin-fieldset">
            <legend>{t("presentation")}</legend>
            <p className="admin-meta">{t("presentationHint")}</p>
            {locales.map((l) => (
              <div key={l} className="admin-fields" lang={l} dir={l === "ar" ? "rtl" : "ltr"}>
                <FormField
                  label={t("appTitle", { language: language(l) })}
                  name={`title-${d.id}-${l}`}
                  maxLength={60}
                  value={texts[d.id]?.appTitle[l] ?? ""}
                  onChange={(e) => setText(d.id, "appTitle", l, e.target.value)}
                />
                <FormField
                  label={t("description", { language: language(l) })}
                  name={`description-${d.id}-${l}`}
                  maxLength={160}
                  value={texts[d.id]?.description[l] ?? ""}
                  onChange={(e) => setText(d.id, "description", l, e.target.value)}
                />
                <TextAreaField
                  label={t("signInMessage", { language: language(l) })}
                  name={`message-${d.id}-${l}`}
                  maxLength={280}
                  rows={2}
                  value={texts[d.id]?.signInMessage[l] ?? ""}
                  onChange={(e) => setText(d.id, "signInMessage", l, e.target.value)}
                />
              </div>
            ))}
          </fieldset>

          <div className="ui-control-row">
            {d.state !== "verified" ? (
              <Button variant="primary" disabled={busy !== null} onClick={() => void verify(d)}>
                {t("verify")}
              </Button>
            ) : null}
            <Button variant="secondary" disabled={busy !== null} onClick={() => void save(d)}>
              {t("save")}
            </Button>
            <ConfirmAction
              label={t("remove")}
              question={t("removeQuestion", { host: d.host })}
              confirmLabel={t("removeConfirm")}
              cancelLabel={t("cancel")}
              onConfirm={() => remove(d)}
            />
          </div>
        </section>
      ))}
    </div>
  );
}
