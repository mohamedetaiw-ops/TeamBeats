"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { CatalogueLayout, CatalogueTemplate } from "@/lib/experience/builder-types";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { LayoutPreview } from "./LayoutPreview";

type Unit = { id: string; name: string; level: number };

type Recommendation = { templateId: string; variant: string; reasons: string[] };

const PURPOSES = ["general", "news", "department", "project", "knowledge", "services", "campaign", "dashboard", "onboarding"] as const;
const VIEWPORTS = ["desktop", "tablet", "mobile"] as const;
const DENSITIES = ["light", "medium", "heavy"] as const;

const SLUG = /^[a-z0-9]+(-[a-z0-9]+)*(\/[a-z0-9]+(-[a-z0-9]+)*)*$/;

/**
 * Template chooser (spec 032 FR-017): every template shows a preview and a text description with its regions, so the
 * choice never depends on seeing the picture. The address is checked as the API checks it, and the API has the last word
 * on the address and on whether the editor may create a page at the chosen scope.
 */
export function TemplateChooser({
  templates,
  layouts,
  units,
  tenantWide,
}: {
  templates: CatalogueTemplate[];
  layouts: CatalogueLayout[];
  units: Unit[];
  tenantWide: boolean;
}) {
  const t = useTranslations("builder");
  const router = useRouter();
  const [templateId, setTemplateId] = useState(templates[0]?.id ?? "");
  const [variant, setVariant] = useState("");
  const [slug, setSlug] = useState("");
  const [scope, setScope] = useState(tenantWide ? "" : (units[0]?.id ?? ""));
  const [slugError, setSlugError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [facts, setFacts] = useState<{ purpose: string; viewport: string; density: string } | null>(null);
  const [advice, setAdvice] = useState<{ key: string; items: Recommendation[] } | null>(null);
  const factsKey = facts ? `${facts.purpose}|${facts.viewport}|${facts.density}` : "";
  const recommended = advice && advice.key === factsKey ? advice.items : [];

  // Spec 032 FR-023: the server ranks templates from the facts; the advice only marks cards and never chooses for the editor.
  useEffect(() => {
    if (!facts) return;
    const key = `${facts.purpose}|${facts.viewport}|${facts.density}`;
    const query = new URLSearchParams(facts).toString();
    let live = true;
    fetch(`/api/v1/experience/template-recommendations?${query}`)
      .then((r) => (r.ok ? (r.json() as Promise<{ recommendations: Recommendation[] }>) : { recommendations: [] }))
      .then((v) => {
        if (live) setAdvice({ key, items: v.recommendations });
      })
      .catch(() => {
        if (live) setAdvice({ key, items: [] });
      });
    return () => {
      live = false;
    };
  }, [facts]);

  const setFact = (name: "purpose" | "viewport" | "density", value: string) =>
    setFacts((f) => ({ purpose: "general", viewport: "desktop", density: "medium", ...f, [name]: value }));

  const byId = new Map(layouts.map((l) => [l.id, l]));
  const chosen = templates.find((x) => x.id === templateId);
  const variants = chosen?.variants ?? [];
  const chosenVariant = variants.includes(variant) ? variant : (variants[0] ?? "");
  const regionName = (id: string) => t(`regions.${id}`);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setFormError(null);
    if (!SLUG.test(slug)) {
      setSlugError(t("create.slugInvalid"));
      return;
    }
    setSlugError(null);
    setBusy(true);
    try {
      const response = await fetch("/api/v1/experience/pages", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ templateId, slug, orgUnitId: scope || null, variant: chosenVariant || null }),
      });
      if (response.ok) {
        const page = (await response.json()) as { id: string };
        router.push(`/admin/pages/${page.id}`);
        return;
      }
      if (response.status === 409) setSlugError(t("create.slugTaken"));
      else if (response.status === 400) setSlugError(t("create.slugInvalid"));
      else if (response.status === 403) setFormError(t("create.scopeDenied"));
      else setFormError(t("create.failed"));
    } catch {
      setFormError(t("create.failed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="builder-create" onSubmit={(e) => void submit(e)} noValidate>
      <fieldset className="builder-recommend" data-testid="template-recommend">
        <legend>{t("recommend.title")}</legend>
        <p className="builder-template-description">{t("recommend.intro")}</p>
        <div className="builder-recommend-facts">
          <SelectField
            label={t("recommend.purpose")}
            name="purpose"
            value={facts?.purpose ?? ""}
            onChange={(e) => setFact("purpose", e.target.value)}
            options={[{ value: "", label: t("recommend.choose") }, ...PURPOSES.map((p) => ({ value: p, label: t(`recommend.purposes.${p}`) }))]}
          />
          <SelectField
            label={t("recommend.viewport")}
            name="viewport"
            value={facts?.viewport ?? "desktop"}
            onChange={(e) => setFact("viewport", e.target.value)}
            options={VIEWPORTS.map((v) => ({ value: v, label: t(`recommend.viewports.${v}`) }))}
          />
          <SelectField
            label={t("recommend.density")}
            name="density"
            value={facts?.density ?? "medium"}
            onChange={(e) => setFact("density", e.target.value)}
            options={DENSITIES.map((d) => ({ value: d, label: t(`recommend.densities.${d}`) }))}
          />
        </div>
        <p className="builder-template-description" aria-live="polite">
          {facts ? (recommended.length > 0 ? t("recommend.found", { count: recommended.length }) : t("recommend.none")) : ""}
        </p>
      </fieldset>

      <fieldset className="builder-templates">
        <legend>{t("create.template")}</legend>
        <div className="builder-template-grid">
          {templates.map((x) => {
            const layout = byId.get(x.layout);
            const regions = x.regions.map((r) => regionName(r.id)).join(t("create.listSeparator"));
            const advised = recommended.find((r) => r.templateId === x.id);
            return (
              <label key={x.id} className="builder-template" data-selected={x.id === templateId || undefined} data-recommended={advised ? true : undefined}>
                <input
                  type="radio"
                  name="template"
                  value={x.id}
                  checked={x.id === templateId}
                  onChange={() => {
                    setTemplateId(x.id);
                    if (advised) setVariant(advised.variant);
                  }}
                />
                {layout ? <LayoutPreview layout={layout} regionName={regionName} /> : null}
                <span className="builder-template-name">{x.name}</span>
                <span className="builder-template-description">{x.description}</span>
                <span className="builder-template-regions">{t("create.regions", { regions })}</span>
                {advised ? (
                  <span className="builder-template-advice">
                    <Badge tone="info" icon="spark">
                      {t("recommend.badge")}
                    </Badge>
                    <span>{advised.reasons.map((r) => t(`recommend.reasons.${r}`)).join(t("create.listSeparator"))}</span>
                  </span>
                ) : null}
              </label>
            );
          })}
        </div>
      </fieldset>

      {variants.length > 1 ? (
        <div className="field">
          <label htmlFor="builder-variant">{t("create.variant")}</label>
          <select id="builder-variant" value={chosenVariant} onChange={(e) => setVariant(e.target.value)}>
            {variants.map((v) => (
              <option key={v} value={v}>
                {t(`variants.${v}`)}
              </option>
            ))}
          </select>
        </div>
      ) : null}

      <FormField
        label={t("create.slug")}
        name="slug"
        hint={t("create.slugHint")}
        value={slug}
        dir="ltr"
        autoComplete="off"
        spellCheck={false}
        error={slugError}
        onChange={(e) => setSlug(e.target.value.trim().toLowerCase())}
      />

      <div className="field">
        <label htmlFor="builder-scope">{t("create.scope")}</label>
        <select id="builder-scope" value={scope} onChange={(e) => setScope(e.target.value)}>
          {tenantWide ? <option value="">{t("pages.wholeTenant")}</option> : null}
          {units.map((u) => (
            <option key={u.id} value={u.id}>
              {u.name}
            </option>
          ))}
        </select>
      </div>

      {formError ? (
        <p className="field-error" role="alert">
          {formError}
        </p>
      ) : null}
      <div className="builder-actions">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("create.creating")} disabled={!chosen}>
          {t("create.submit")}
        </Button>
      </div>
    </form>
  );
}
