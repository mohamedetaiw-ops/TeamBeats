"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Avatar } from "@/components/ui/Avatar";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { Icon } from "@/components/ui/Icon";
import { Widget } from "@/components/ui/Widget";
import { dateLong, joinMeta, languageName, timeZoneLabel, timeZones } from "@/lib/format";
import { problemMessages } from "@/lib/notifications/types";
import { isPhoneField, localName, locationName, personName, photoAt, type OwnField, type OwnProfile } from "@/lib/people/types";
import "./people.css";

/** The groups of the profile page (spec 036 DLS-063); custom fields and any field not listed go to Other. */
const groups: { key: "work" | "contact" | "preferences" | "other"; fields: readonly string[] }[] = [
  { key: "work", fields: ["jobTitle", "department", "office", "employeeNumber", "hireDate", "grade", "costCentre", "employmentType"] },
  { key: "contact", fields: ["email", "workPhone", "mobilePhone"] },
  { key: "preferences", fields: ["preferredLanguage", "timeZone"] },
  { key: "other", fields: [] },
];
const listed = new Set(groups.flatMap((g) => g.fields));

/**
 * The person's own profile settings (spec 010 US1; spec 036 DLS-063): an identity header, then Work information,
 * Contact details and Personal preferences. A group whose read-only fields all come from one source says so once
 * ("Managed by the directory"), and a field carries an eye label only when fewer people than everyone can see it. Only
 * fields the API marks editable (person-owned) can be changed; the API checks this again on save.
 */
export function OwnProfileForm({ initial }: { initial: OwnProfile }) {
  const t = useTranslations("people");
  const locale = useLocale();
  const [profile, setProfile] = useState(initial);
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(initial.fields.map((f) => [f.key, f.value ?? ""])));
  const [optOut, setOptOut] = useState(initial.newcomerOptOut);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  // The browser's zone list can differ from the server's, so the full list is filled in after the first render.
  const [zones, setZones] = useState<string[] | null>(null);
  useEffect(() => {
    const start = setTimeout(() => setZones(timeZones(null)), 0);
    return () => clearTimeout(start);
  }, []);

  const label = (f: OwnField) => (f.names ? localName(f.names, locale, f.key) : t(`field.${f.key}` as "field.email"));
  const shown = (f: OwnField) => {
    if (!f.value) return t("me.empty");
    if (f.key === "preferredLanguage") return languageName(f.value, locale);
    if (f.key === "hireDate") return dateLong(f.value, locale);
    if (f.key === "timeZone") return timeZoneLabel(f.value, locale);
    return f.value;
  };
  const visibility = (f: OwnField) =>
    f.visibility === "everyone" ? null : (
      <span className="people-visibility" title={t("me.visibility", { value: t(`visibility.${f.visibility}` as "visibility.everyone") })}>
        <Icon name="eye" size={14} />
        {t(`visibility.${f.visibility}` as "visibility.everyone")}
      </span>
    );

  // The photo (FR-013): sent as the file itself; the API checks its type and size, removes its metadata and resizes it.
  const [photo, setPhoto] = useState<string | null>(initial.profile.photo ?? null);
  const [photoBusy, setPhotoBusy] = useState(false);
  const [photoResult, setPhotoResult] = useState<{ ok: boolean; messages: string[] } | null>(null);

  async function changePhoto(method: "PUT" | "DELETE", file?: File) {
    setPhotoBusy(true);
    setPhotoResult(null);
    try {
      const response = await fetch("/api/v1/me/photo", {
        method,
        headers: file ? { "content-type": file.type || "application/octet-stream" } : undefined,
        body: file,
      });
      if (response.ok) {
        setPhoto(((await response.json()) as { photo: string | null }).photo);
        setPhotoResult({ ok: true, messages: [t(method === "PUT" ? "me.photo.saved" : "me.photo.removed")] });
      } else {
        setPhotoResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setPhotoResult({ ok: false, messages: [t("failed")] });
    } finally {
      setPhotoBusy(false);
    }
  }

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

  const person = profile.profile;
  const name = personName(person, locale);
  const title = profile.fields.find((f) => f.key === "jobTitle")?.value ?? null;
  const editor = (f: OwnField) => {
    const common = { label: label(f), name: f.key };
    if (f.key === "timeZone") {
      return (
        <SelectField
          key={f.key}
          {...common}
          value={values[f.key] ?? ""}
          onChange={(e) => setValues({ ...values, [f.key]: e.target.value })}
          options={[{ value: "", label: t("me.empty") }, ...(zones ? (f.value && !zones.includes(f.value) ? [f.value, ...zones] : zones) : f.value ? [f.value] : []).map((z) => ({ value: z, label: timeZoneLabel(z, locale) }))]}
        />
      );
    }
    return (
      <FormField
        key={f.key}
        {...common}
        type={f.key === "hireDate" ? "date" : isPhoneField(f.key) ? "tel" : f.key === "email" ? "email" : "text"}
        dir={isPhoneField(f.key) || f.key === "email" ? "ltr" : undefined}
        value={values[f.key] ?? ""}
        onChange={(e) => setValues({ ...values, [f.key]: e.target.value })}
      />
    );
  };

  return (
    <form
      className="people-profile"
      aria-labelledby="own-profile-title"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <h2 id="own-profile-title" className="visually-hidden">
        {t("me.title")}
      </h2>
      <section className="people-identity" aria-label={name}>
        <Avatar name={name} size="lg" src={photoAt(photo, 64)} />
        <div className="people-identity-text">
          <p className="people-identity-name">
            <bdi>{name}</bdi>
          </p>
          {title ? (
            <p className="people-identity-title">
              <bdi>{title}</bdi>
            </p>
          ) : null}
          <p className="people-meta">
            {joinMeta([person.unit ? localName(person.unit.names, locale) : null, person.location ? locationName(person.location, locale) : null])}
          </p>
          <div className="people-photo-actions">
            <label className="button button-secondary" data-size="sm" aria-disabled={photoBusy || undefined}>
              <Icon name="upload" size={16} />
              <span>{photo ? t("me.photo.change") : t("me.photo.add")}</span>
              <input
                type="file"
                className="visually-hidden"
                accept="image/png,image/jpeg"
                disabled={photoBusy}
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  e.target.value = "";
                  if (file) void changePhoto("PUT", file);
                }}
              />
            </label>
            {photo ? (
              <Button type="button" variant="quiet" size="sm" icon="trash" busy={photoBusy} onClick={() => void changePhoto("DELETE")}>
                {t("me.photo.remove")}
              </Button>
            ) : null}
            <span className="field-hint">{t("me.photo.hint")}</span>
          </div>
          <p aria-live="polite" className="people-photo-result">
            {photoResult ? (
              <span className={photoResult.ok ? undefined : "field-error"} role={photoResult.ok ? undefined : "alert"}>
                {photoResult.messages.join(" ")}
              </span>
            ) : null}
          </p>
        </div>
        <Link className="button button-secondary" href={`/people/${person.id}`}>
          <Icon name="eye" size={16} />
          {t("me.view")}
        </Link>
      </section>

      {groups.map((g) => {
        const fields = profile.fields.filter((f) => (g.key === "other" ? !listed.has(f.key) : g.fields.includes(f.key)));
        if (fields.length === 0) return null;
        const fixed = fields.filter((f) => !f.editable);
        const sources = new Set(fixed.map((f) => f.source));
        const oneSource = sources.size === 1 ? [...sources][0]! : null;
        const editable = fields.filter((f) => f.editable);
        return (
          <Widget
            key={g.key}
            title={t(`me.groups.${g.key}`)}
            headingLevel={3}
            count={
              oneSource && oneSource !== "person" ? (
                <span className="people-managed">
                  <Icon name="lock" size={14} />
                  {t(`me.managed.${oneSource}` as "me.managed.directory")}
                </span>
              ) : undefined
            }
          >
            {fixed.length > 0 ? (
              <dl className="people-details people-details-group">
                {fixed.map((f) => (
                  <div key={f.key} className="people-pair">
                    <dt>{label(f)}</dt>
                    <dd>
                      <bdi dir={isPhoneField(f.key) || f.key === "email" ? "ltr" : undefined}>{shown(f)}</bdi>
                      {!oneSource ? (
                        <span className="people-source">
                          <Icon name="lock" size={12} />
                          {t(`source.${f.source}` as "source.directory")}
                        </span>
                      ) : null}
                      {visibility(f)}
                    </dd>
                  </div>
                ))}
              </dl>
            ) : null}
            {editable.length > 0 ? (
              <div className="people-edit-grid">
                {editable.map((f) => (
                  <div key={f.key} className="people-edit">
                    {editor(f)}
                    {visibility(f)}
                  </div>
                ))}
              </div>
            ) : null}
          </Widget>
        );
      })}

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
      </div>
    </form>
  );
}
