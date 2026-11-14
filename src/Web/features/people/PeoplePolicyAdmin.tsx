"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { localName, visibilities, type Location, type Policy, type PolicyField } from "@/lib/people/types";
import "./people.css";
import { EmptyCell } from "@/components/ui/EmptyCell";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * People directory administration (spec 010 US1 and US5): the source and visibility of each field, tenant fields, the
 * new joiner period, the location attribute, and location names. The API checks the right, the plan and the values on
 * every save; the policy is saved with its version so two administrators do not overwrite each other.
 */
export function PeoplePolicyAdmin({ initialPolicy, initialLocations }: { initialPolicy: Policy; initialLocations: Location[] }) {
  const t = useTranslations("people");
  const locale = useLocale();
  const [policy, setPolicy] = useState(initialPolicy);
  const [fields, setFields] = useState<PolicyField[]>(initialPolicy.fields);
  const [days, setDays] = useState(String(initialPolicy.newcomerDays));
  const [optOut, setOptOut] = useState(initialPolicy.allowNewcomerOptOut);
  const [attribute, setAttribute] = useState(initialPolicy.locationAttribute);
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);

  const label = (f: PolicyField) => (f.custom ? localName(f.names, locale, f.key) : t(`field.${f.key}` as "field.email"));
  const update = (key: string, change: Partial<PolicyField>) => setFields(fields.map((f) => (f.key === key ? { ...f, ...change } : f)));
  const builtIn = fields.filter((f) => !f.custom);
  const custom = fields.filter((f) => f.custom);

  function addCustom() {
    const names: Record<string, string> = {};
    if (nameEn.trim()) names.en = nameEn.trim();
    if (nameAr.trim()) names.ar = nameAr.trim();
    const key = `custom.f${Date.now().toString(36)}`;
    setFields([...fields, { key, source: "person", visibility: "everyone", sources: ["person", "admin"], custom: true, names }]);
    setNameEn("");
    setNameAr("");
  }

  async function save() {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch("/api/v1/people/policy", {
        method: "PUT",
        headers: { "content-type": "application/json", "if-match": policy.eTag },
        body: JSON.stringify({
          fields: fields.map((f) => ({ key: f.key, source: f.source, visibility: f.visibility, names: f.custom ? f.names : undefined })),
          newcomerDays: Number(days),
          allowNewcomerOptOut: optOut,
          locationAttribute: attribute,
        }),
      });
      if (response.ok) {
        const saved = (await response.json()) as Policy;
        setPolicy(saved);
        setFields(saved.fields);
        setResult({ ok: true, messages: [t("admin.saved")] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const row = (f: PolicyField) => (
    <tr key={f.key}>
      <th scope="row">
        <bdi>{label(f)}</bdi>
      </th>
      <td>
        <select aria-label={`${label(f)}: ${t("admin.source")}`} value={f.source} onChange={(e) => update(f.key, { source: e.target.value })}>
          {f.sources.map((s) => (
            <option key={s} value={s}>
              {t(`source.${s}` as "source.directory")}
            </option>
          ))}
        </select>
      </td>
      <td>
        <select aria-label={`${label(f)}: ${t("admin.visibility")}`} value={f.visibility} onChange={(e) => update(f.key, { visibility: e.target.value })}>
          {visibilities.map((v) => (
            <option key={v} value={v}>
              {t(`visibility.${v}`)}
            </option>
          ))}
        </select>
      </td>
      {custom.length > 0 ? (
        <td>
          {f.custom ? (
            <Button variant="destructive" onClick={() => setFields(fields.filter((x) => x.key !== f.key))}>
              {t("admin.remove", { name: label(f) })}
            </Button>
          ) : (
            <EmptyCell />
          )}
        </td>
      ) : null}
    </tr>
  );

  return (
    <div className="people">
      <p className="people-meta">
        {policy.directorySyncedAt
          ? t("admin.synced", { time: new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(policy.directorySyncedAt)) })
          : t("admin.notSynced")}
      </p>
      <form
        className="people"
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        <section aria-labelledby="people-admin-fields" className="people-section">
          <h2 id="people-admin-fields">{t("admin.fields")}</h2>
          <div className="people-table-wrap">
            <table className="people-table" data-testid="people-policy">
              <thead>
                <tr>
                  <th scope="col">{t("admin.field")}</th>
                  <th scope="col">{t("admin.source")}</th>
                  <th scope="col">{t("admin.visibility")}</th>
                  {custom.length > 0 ? <th scope="col">{t("admin.actions")}</th> : null}
                </tr>
              </thead>
              <tbody>
                {builtIn.map(row)}
                {custom.map(row)}
              </tbody>
            </table>
          </div>
        </section>

        <section aria-labelledby="people-admin-custom" className="people-section">
          <h2 id="people-admin-custom">{t("admin.custom")}</h2>
          {policy.customFieldsAvailable ? (
            <>
              <p>{t("admin.customHint")}</p>
              <div className="people-search">
                <FormField label={t("admin.nameEn")} name="customNameEn" value={nameEn} onChange={(e) => setNameEn(e.target.value)} maxLength={80} />
                <FormField label={t("admin.nameAr")} name="customNameAr" dir="rtl" value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={80} />
                <Button onClick={addCustom} disabled={!nameEn.trim() && !nameAr.trim()}>
                  {t("admin.add")}
                </Button>
              </div>
            </>
          ) : (
            <p>{t("admin.customUnavailable")}</p>
          )}
        </section>

        <section aria-labelledby="people-admin-settings" className="people-section">
          <h2 id="people-admin-settings">{t("admin.settings")}</h2>
          <FormField label={t("admin.newcomerDays")} name="newcomerDays" type="number" min={7} max={90} hint={t("admin.newcomerHint")} value={days} onChange={(e) => setDays(e.target.value)} />
          <label className="people-check">
            <input type="checkbox" checked={optOut} onChange={(e) => setOptOut(e.target.checked)} />
            {t("admin.allowOptOut")}
          </label>
          <label className="field people-select">
            <span>{t("admin.locationAttribute")}</span>
            <select value={attribute} onChange={(e) => setAttribute(e.target.value)}>
              {policy.locationAttributes.map((a) => (
                <option key={a} value={a}>
                  {t(`admin.attr.${a}` as "admin.attr.officeLocation")}
                </option>
              ))}
            </select>
          </label>
        </section>

        <div aria-live="polite">
          {result ? (
            <div className={result.ok ? "notice" : "notice notice-error"} role={result.ok ? "status" : "alert"}>
              {result.messages.map((m) => (
                <p key={m}>{m}</p>
              ))}
            </div>
          ) : null}
        </div>
        <div>
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("me.saving")}>
            {t("admin.save")}
          </Button>
        </div>
      </form>

      <Locations initial={initialLocations} />
    </div>
  );
}

function Locations({ initial }: { initial: Location[] }) {
  const t = useTranslations("people");
  const locale = useLocale();
  const [locations, setLocations] = useState(initial);
  const [result, setResult] = useState<{ key: string; ok: boolean; messages: string[] } | null>(null);

  async function rename(location: Location, names: Record<string, string>) {
    setResult(null);
    try {
      const response = await fetch(`/api/v1/people/locations/${encodeURIComponent(location.key)}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ names }),
      });
      if (response.ok) {
        const saved = (await response.json()) as Location;
        setLocations(locations.map((l) => (l.key === saved.key ? saved : l)));
        setResult({ key: location.key, ok: true, messages: [t("admin.renamed")] });
      } else {
        setResult({ key: location.key, ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ key: location.key, ok: false, messages: [t("failed")] });
    }
  }

  return (
    <section aria-labelledby="people-admin-locations" className="people-section">
      <h2 id="people-admin-locations">{t("admin.locations")}</h2>
      {locations.length === 0 ? <p>{t("admin.noLocations")}</p> : null}
      <ul className="people-list" data-testid="people-locations">
        {locations.map((l) => (
          <li key={l.key}>
            <form
              className="people-form-row"
              onSubmit={(e) => {
                e.preventDefault();
                const data = new FormData(e.currentTarget);
                const names: Record<string, string> = {};
                for (const lang of ["en", "ar"]) {
                  const value = String(data.get(lang) ?? "").trim();
                  if (value) names[lang] = value;
                }
                void rename(l, names);
              }}
            >
              <h3>
                <bdi>{l.names[locale] ?? l.label}</bdi>
              </h3>
              <p className="people-meta">
                <bdi>{t("admin.locationLabel", { label: l.label })}</bdi> <span>{t("admin.locationPeople", { count: l.people })}</span>
              </p>
              <div className="people-search">
                <FormField label={t("admin.nameEn")} name="en" defaultValue={l.names.en ?? ""} maxLength={80} />
                <FormField label={t("admin.nameAr")} name="ar" dir="rtl" defaultValue={l.names.ar ?? ""} maxLength={80} />
                <Button type="submit">{t("admin.rename")}</Button>
              </div>
              {result?.key === l.key ? (
                <div className={result.ok ? "notice" : "notice notice-error"} role={result.ok ? "status" : "alert"}>
                  {result.messages.map((m) => (
                    <p key={m}>{m}</p>
                  ))}
                </div>
              ) : null}
            </form>
          </li>
        ))}
      </ul>
    </section>
  );
}
