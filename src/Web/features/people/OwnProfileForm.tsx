"use client";

import Link from "next/link";
import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { localName, type OwnField, type OwnProfile } from "@/lib/people/types";
import "./people.css";

/**
 * The person's own profile settings (spec 010 US1): every field with its source and who sees it. Only fields the API
 * marks editable (person-owned) can be changed; the API checks this again on save.
 */
export function OwnProfileForm({ initial }: { initial: OwnProfile }) {
  const t = useTranslations("people");
  const locale = useLocale();
  const [profile, setProfile] = useState(initial);
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(initial.fields.map((f) => [f.key, f.value ?? ""])));
  const [optOut, setOptOut] = useState(initial.newcomerOptOut);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);

  const label = (f: OwnField) => (f.names ? localName(f.names, locale, f.key) : t(`field.${f.key}` as "field.email"));
  const describe = (f: OwnField) =>
    `${t("me.source", { value: t(`source.${f.source}` as "source.directory") })} ${t("me.visibility", { value: t(`visibility.${f.visibility}` as "visibility.everyone") })}`;

  async function save() {
    setBusy(true);
    setResult(null);
    const changed: Record<string, string | null> = {};
    for (const f of profile.fields.filter((x) => x.editable)) {
      const next = values[f.key]?.trim() ?? "";
      if (next !== (f.value ?? "")) changed[f.key] = next === "" ? null : next;
    }
    try {
      const response = await fetch("/api/v1/me/profile", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ values: changed, newcomerOptOut: profile.allowNewcomerOptOut ? optOut : undefined }),
      });
      if (response.ok) {
        const saved = (await response.json()) as OwnProfile;
        setProfile(saved);
        setValues(Object.fromEntries(saved.fields.map((f) => [f.key, f.value ?? ""])));
        setOptOut(saved.newcomerOptOut);
        setResult({ ok: true, messages: [t("me.saved")] });
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
    <form
      className="people-section people-form"
      aria-labelledby="own-profile-title"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <h2 id="own-profile-title" className="visually-hidden">
        {t("me.title")}
      </h2>
      <dl className="people-details">
        {profile.fields
          .filter((f) => !f.editable)
          .map((f) => (
            <div key={f.key} className="people-pair">
              <dt>{label(f)}</dt>
              <dd>
                <bdi>{f.value ?? t("me.empty")}</bdi>
                <div className="people-meta">{describe(f)}</div>
              </dd>
            </div>
          ))}
      </dl>
      {profile.fields
        .filter((f) => f.editable)
        .map((f) => (
          <FormField
            key={f.key}
            label={label(f)}
            name={f.key}
            hint={describe(f)}
            type={f.key === "hireDate" ? "date" : f.key === "workPhone" || f.key === "mobilePhone" ? "tel" : "text"}
            dir={f.key === "workPhone" || f.key === "mobilePhone" ? "ltr" : undefined}
            value={values[f.key] ?? ""}
            onChange={(e) => setValues({ ...values, [f.key]: e.target.value })}
          />
        ))}
      {profile.allowNewcomerOptOut ? (
        <label className="people-check">
          <input type="checkbox" checked={optOut} onChange={(e) => setOptOut(e.target.checked)} />
          {t("me.optOut")}
        </label>
      ) : null}
      <div aria-live="polite">
        {result ? (
          <div className={result.ok ? "notice" : "notice notice-error"} role={result.ok ? "status" : "alert"}>
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>
      <div className="people-actions">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("me.saving")}>
          {t("me.save")}
        </Button>
        <Link href={`/people/${profile.profile.id}`}>{t("me.view")}</Link>
      </div>
    </form>
  );
}
