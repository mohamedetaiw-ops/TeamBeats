"use client";

import { useState, type CSSProperties } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { BrandScopePreview } from "@/lib/brand/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import type { PreviewSample } from "./BrandPreview";
import { ContrastTable } from "./ContrastTable";

const devices = ["phone", "tablet", "desktop"] as const;

/**
 * Previews a brand on a sample shell, page and components (spec 033 R2 T016): the organization, draft or version in force,
 * mode, density, language and device are chosen here; the custom properties and the checks come from the API for that
 * organization, so the sample shows what the theme CSS would serve there.
 */
export function ThemePreviewer({
  brandId,
  initial,
  units,
  samples,
}: {
  brandId: string;
  initial: BrandScopePreview;
  units: OrgUnit[];
  samples: { en: PreviewSample; ar: PreviewSample };
}) {
  const t = useTranslations("brand.previewPage");
  const tb = useTranslations("brand");
  const locale = useLocale();
  const [preview, setPreview] = useState(initial);
  const [scope, setScope] = useState(initial.scope);
  const [source, setSource] = useState<string>(initial.source);
  const [mode, setMode] = useState("light");
  const [density, setDensity] = useState("comfortable");
  const [language, setLanguage] = useState<"en" | "ar">(locale === "ar" ? "ar" : "en");
  const [device, setDevice] = useState<(typeof devices)[number]>("desktop");
  const [failed, setFailed] = useState(false);
  const [loading, setLoading] = useState(false);
  const modeName = (m: string) => (["light", "dark", "high_contrast", "comfortable", "compact"].includes(m) ? tb(`modes.${m}`) : m);

  async function load(nextScope: string, nextSource: string) {
    setScope(nextScope);
    setSource(nextSource);
    setLoading(true);
    setFailed(false);
    try {
      const query = new URLSearchParams({ scope: nextScope, source: nextSource });
      const response = await fetch(`/api/v1/experience/brands/${brandId}/preview?${query}`, { cache: "no-store" });
      if (response.ok) setPreview((await response.json()) as BrandScopePreview);
      else setFailed(true);
    } catch {
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }

  const s = samples[language];
  const vars = (preview.variants[`${mode}.${density}`] ?? {}) as CSSProperties;
  const scopeLabel = preview.scope === "tenant" ? t("wholeOrganization") : (preview.scopeNames[locale] ?? preview.scopeNames.en ?? "");

  return (
    <div className="brand-previewer" data-testid="theme-previewer">
      <section className="brand-previewer-controls" aria-label={t("controls")}>
        <div className="field">
          <label htmlFor="theme-preview-scope">{t("organization")}</label>
          <select id="theme-preview-scope" value={scope} onChange={(e) => void load(e.target.value, source)}>
            <option value="tenant">{t("wholeOrganization")}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {unitName(u, locale)}
              </option>
            ))}
          </select>
        </div>
        <SegmentedControl
          label={t("source")}
          name="theme-preview-source"
          value={source}
          onChange={(v) => void load(scope, v)}
          options={[
            { value: "draft", label: t("sources.draft") },
            { value: "current", label: t("sources.current") },
          ]}
        />
        <SegmentedControl
          label={t("mode")}
          name="theme-preview-mode"
          value={mode}
          onChange={setMode}
          options={[
            { value: "light", label: modeName("light") },
            { value: "dark", label: modeName("dark") },
            { value: "high_contrast", label: modeName("high_contrast") },
          ]}
        />
        <SegmentedControl
          label={t("density")}
          name="theme-preview-density"
          value={density}
          onChange={setDensity}
          options={[
            { value: "comfortable", label: modeName("comfortable") },
            { value: "compact", label: modeName("compact") },
          ]}
        />
        <SegmentedControl
          label={t("language")}
          name="theme-preview-language"
          value={language}
          onChange={(v) => setLanguage(v === "ar" ? "ar" : "en")}
          options={[
            { value: "en", label: t("languages.en") },
            { value: "ar", label: t("languages.ar") },
          ]}
        />
        <SegmentedControl
          label={t("device")}
          name="theme-preview-device"
          value={device}
          onChange={(v) => setDevice(devices.find((d) => d === v) ?? "desktop")}
          options={devices.map((d) => ({ value: d, label: t(`devices.${d}`) }))}
        />
      </section>

      <div aria-live="polite">
        {failed ? (
          <Alert tone="danger" title={t("failedTitle")} live>
            {t("failed")}
          </Alert>
        ) : null}
        {!preview.inherits ? (
          <Alert tone="info" title={t("otherBrandTitle")}>
            {t("otherBrand", { scope: scopeLabel })}
          </Alert>
        ) : null}
        <p className="notice" data-testid="theme-preview-caption">
          {loading ? t("loading") : t(preview.source === "draft" ? "captionDraft" : "captionCurrent", { scope: scopeLabel })}
        </p>
      </div>

      <div className="brand-stage" data-device={device}>
        <figure
          className="brand-stage-frame"
          data-mode={mode}
          dir={language === "ar" ? "rtl" : "ltr"}
          lang={language}
          style={vars}
          aria-label={t("frameLabel", { scope: scopeLabel, mode: modeName(mode), device: t(`devices.${device}`) })}
          data-testid="theme-preview-frame"
        >
          <header className="brand-stage-shell">
            <span className="brand-stage-title">{s.shell}</span>
            <nav aria-label={t("sampleNav")} className="brand-stage-nav">
              <span aria-current="page">{s.navHome}</span>
              <span>{s.navNews}</span>
            </nav>
          </header>
          <div className="brand-stage-page">
            <h2 className="brand-stage-heading">{s.heading}</h2>
            <p>{s.body}</p>
            <p className="brand-stage-secondary">{s.secondary}</p>
            <div className="brand-stage-card">
              <p>{s.body}</p>
              <span className="brand-stage-link">{s.link}</span>
            </div>
            <FormField label={s.heading} name="theme-preview-sample" defaultValue={s.secondary} readOnly />
            <div className="button-row">
              <Button variant="primary">{s.primary}</Button>
              <Button variant="secondary">{s.secondaryAction}</Button>
            </div>
            <div className="brand-stage-states">
              <Badge tone="success">{s.success}</Badge>
              <Badge tone="warning">{s.warning}</Badge>
              <Badge tone="danger">{s.danger}</Badge>
            </div>
          </div>
        </figure>
      </div>

      <ContrastTable
        findings={preview.findings}
        locale={locale}
        labels={{
          title: tb("contrast.title"),
          token: tb("contrast.token"),
          against: tb("contrast.against"),
          mode: tb("contrast.mode"),
          ratio: tb("contrast.ratio"),
          required: tb("contrast.required"),
          result: tb("contrast.result"),
          blocking: tb("contrast.blocking"),
          warning: tb("contrast.warning"),
          empty: tb("contrast.empty"),
          modeName,
        }}
      />
    </div>
  );
}
