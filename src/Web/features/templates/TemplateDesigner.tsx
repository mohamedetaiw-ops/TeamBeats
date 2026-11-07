"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import type { ProblemErrors } from "@/lib/brand/types";
import { COMPONENT_CATEGORIES, PROTECTIONS, type RegionPolicy, type TenantTemplate } from "@/lib/experience/builder-types";

type Unit = { id: string; name: string; level: number };
type Region = { id: string; policy: RegionPolicy };

/**
 * Template designer (spec 032 US5, FR-019 to FR-021). It sends what the designer chose and shows what the API answers:
 * the API checks every policy and lock, decides when a save starts a new version, and refuses what the plan or the
 * designer's rights do not allow.
 */
export function TemplateDesigner({ template, baseVariants, units }: { template: TenantTemplate; baseVariants: string[]; units: Unit[] }) {
  const t = useTranslations("templates");
  const tb = useTranslations("builder");
  const locale = useLocale();
  const router = useRouter();
  const latest = template.latest;
  const [names, setNames] = useState<Record<string, string>>({ en: latest.names?.en ?? "", ar: latest.names?.ar ?? "" });
  const [descriptions, setDescriptions] = useState<Record<string, string>>({
    en: latest.descriptions?.en ?? "",
    ar: latest.descriptions?.ar ?? "",
  });
  const [variants, setVariants] = useState<string[]>(latest.variants);
  const [regions, setRegions] = useState<Region[]>(latest.regions);
  const [distribution, setDistribution] = useState<string[]>(template.distribution);
  const [errors, setErrors] = useState<ProblemErrors>({});
  const [status, setStatus] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const fieldError = (field: string) => errors[field]?.[0]?.message;
  const when = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });

  const setPolicy = (id: string, change: Partial<RegionPolicy>) =>
    setRegions((all) => all.map((r) => (r.id === id ? { ...r, policy: { ...r.policy, ...change } } : r)));
  const toggle = (list: string[], value: string) => (list.includes(value) ? list.filter((x) => x !== value) : [...list, value]);
  const filled = (texts: Record<string, string>) => Object.fromEntries(Object.entries(texts).filter(([, v]) => v.trim().length > 0));

  async function send(action: string, path: string, method: string, body: unknown, done: string) {
    setBusy(action);
    setErrors({});
    setStatus(null);
    setFailure(null);
    try {
      const response = await fetch(`/api/v1/experience/templates/${encodeURIComponent(template.id)}${path}`, {
        method,
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body ?? {}),
      });
      if (response.ok) {
        setStatus(done);
        router.refresh();
        return;
      }
      const problem = (await response.json().catch(() => null)) as { errors?: ProblemErrors; title?: string } | null;
      if (response.status === 400 && problem?.errors) setErrors(problem.errors);
      setFailure(response.status === 400 ? t("checkFields") : (problem?.title ?? t("failed")));
    } catch {
      setFailure(t("failed"));
    } finally {
      setBusy(null);
    }
  }

  const save = () =>
    void send("save", "/draft", "PUT", { names: filled(names), descriptions: filled(descriptions), variants, regions }, t("saved"));
  const draft = latest.state === "draft";
  const released = template.versions.some((v) => v.state === "released");

  return (
    <div className="builder-designer">
      <section aria-labelledby="designer-names">
        <h2 id="designer-names">{t("namesTitle")}</h2>
        <FormField label={t("nameEn")} name="nameEn" lang="en" dir="ltr" value={names.en} onChange={(e) => setNames({ ...names, en: e.target.value })} error={fieldError("names")} />
        <FormField label={t("nameAr")} name="nameAr" lang="ar" dir="rtl" value={names.ar} onChange={(e) => setNames({ ...names, ar: e.target.value })} />
        <FormField label={t("descriptionEn")} name="descriptionEn" lang="en" dir="ltr" value={descriptions.en} onChange={(e) => setDescriptions({ ...descriptions, en: e.target.value })} error={fieldError("descriptions")} />
        <FormField label={t("descriptionAr")} name="descriptionAr" lang="ar" dir="rtl" value={descriptions.ar} onChange={(e) => setDescriptions({ ...descriptions, ar: e.target.value })} />
      </section>

      <fieldset aria-describedby={fieldError("variants") ? "designer-variants-error" : undefined}>
        <legend>{t("variantsTitle")}</legend>
        {baseVariants.map((v) => (
          <label key={v} className="builder-check">
            <input type="checkbox" checked={variants.includes(v)} onChange={() => setVariants(toggle(variants, v))} />
            {tb(`variants.${v}`)}
          </label>
        ))}
        {fieldError("variants") ? (
          <p id="designer-variants-error" className="field-error" role="alert">
            {fieldError("variants")}
          </p>
        ) : null}
      </fieldset>

      <section aria-labelledby="designer-regions">
        <h2 id="designer-regions">{t("regionsTitle")}</h2>
        <p className="notice">{t("regionsIntro")}</p>
        {regions.map((region) => {
          const name = tb(`regions.${region.id}`);
          const error = fieldError(`regions.${region.id}`);
          return (
            <fieldset key={region.id} className="builder-region-policy" data-region={region.id}>
              <legend>{name}</legend>
              <div className="field">
                <label htmlFor={`protection-${region.id}`}>{t("protection")}</label>
                <select
                  id={`protection-${region.id}`}
                  value={region.policy.protection}
                  onChange={(e) => setPolicy(region.id, { protection: e.target.value })}
                >
                  {PROTECTIONS.map((p) => (
                    <option key={p} value={p}>
                      {t(`protections.${p}`)}
                    </option>
                  ))}
                </select>
              </div>
              <FormField
                label={t("minPlacements")}
                name={`min-${region.id}`}
                type="number"
                inputMode="numeric"
                min={0}
                value={String(region.policy.minPlacements)}
                onChange={(e) => setPolicy(region.id, { minPlacements: Number(e.target.value) })}
              />
              <FormField
                label={t("maxPlacements")}
                name={`max-${region.id}`}
                type="number"
                inputMode="numeric"
                min={1}
                value={String(region.policy.maxPlacements)}
                onChange={(e) => setPolicy(region.id, { maxPlacements: Number(e.target.value) })}
              />
              <fieldset>
                <legend>{t("categories")}</legend>
                <p className="field-hint">{t("categoriesHint")}</p>
                {COMPONENT_CATEGORIES.map((c) => (
                  <label key={c} className="builder-check">
                    <input
                      type="checkbox"
                      checked={region.policy.allowedCategories.includes(c)}
                      onChange={() => setPolicy(region.id, { allowedCategories: toggle(region.policy.allowedCategories, c) })}
                    />
                    {t(`categoryNames.${c}`)}
                  </label>
                ))}
              </fieldset>
              {error ? (
                <p className="field-error" role="alert">
                  {error}
                </p>
              ) : null}
            </fieldset>
          );
        })}
      </section>

      <div className="builder-actions">
        <Button variant="primary" onClick={save} busy={busy === "save"} busyLabel={t("saving")}>
          {draft ? t("saveDraft") : t("saveNewVersion")}
        </Button>
        <Button onClick={() => void send("release", "/release", "POST", null, t("releasedNow"))} busy={busy === "release"} busyLabel={t("releasing")} disabled={!draft}>
          {t("release")}
        </Button>
        <Button variant="destructive" onClick={() => void send("deprecate", "/deprecate", "POST", null, t("deprecatedNow"))} busy={busy === "deprecate"} busyLabel={t("deprecating")} disabled={!released}>
          {t("deprecate")}
        </Button>
      </div>
      <p role="status" className="builder-template-description">
        {status}
      </p>
      {failure ? (
        <p className="field-error" role="alert">
          {failure}
        </p>
      ) : null}

      <section aria-labelledby="designer-distribution">
        <h2 id="designer-distribution">{t("distributionTitle")}</h2>
        <p className="notice">{t("distributionIntro")}</p>
        <fieldset>
          <legend>{t("distribution")}</legend>
          {units.map((u) => (
            <label key={u.id} className="builder-check" data-level={Math.min(u.level, 3)}>
              <input type="checkbox" checked={distribution.includes(u.id)} onChange={() => setDistribution(toggle(distribution, u.id))} />
              {u.name}
            </label>
          ))}
        </fieldset>
        <p className="builder-template-description">{distribution.length === 0 ? t("everywhere") : t("units", { count: distribution.length })}</p>
        <Button onClick={() => void send("distribute", "/distribution", "PUT", { orgUnitIds: distribution }, t("distributedNow"))} busy={busy === "distribute"} busyLabel={t("saving")}>
          {t("saveDistribution")}
        </Button>
      </section>

      <section aria-labelledby="designer-versions">
        <h2 id="designer-versions">{t("versionsTitle")}</h2>
        <div className="builder-table-wrap">
          <table className="builder-table" data-testid="template-versions">
            <thead>
              <tr>
                <th scope="col">{t("version")}</th>
                <th scope="col">{t("state")}</th>
                <th scope="col">{t("changed")}</th>
              </tr>
            </thead>
            <tbody>
              {template.versions.map((v) => (
                <tr key={v.version}>
                  <th scope="row">{v.version}</th>
                  <td>{t(`states.${v.state}`)}</td>
                  <td>
                    <time dateTime={v.updatedAt}>{when.format(new Date(v.updatedAt))}</time>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
