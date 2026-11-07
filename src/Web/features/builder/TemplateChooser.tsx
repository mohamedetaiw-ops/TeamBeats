"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { CatalogueLayout, CatalogueTemplate } from "@/lib/experience/builder-types";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { LayoutPreview } from "./LayoutPreview";

type Unit = { id: string; name: string; level: number };

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
      <fieldset className="builder-templates">
        <legend>{t("create.template")}</legend>
        <div className="builder-template-grid">
          {templates.map((x) => {
            const layout = byId.get(x.layout);
            const regions = x.regions.map((r) => regionName(r.id)).join(t("create.listSeparator"));
            return (
              <label key={x.id} className="builder-template" data-selected={x.id === templateId || undefined}>
                <input type="radio" name="template" value={x.id} checked={x.id === templateId} onChange={() => setTemplateId(x.id)} />
                {layout ? <LayoutPreview layout={layout} regionName={regionName} /> : null}
                <span className="builder-template-name">{x.name}</span>
                <span className="builder-template-description">{x.description}</span>
                <span className="builder-template-regions">{t("create.regions", { regions })}</span>
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
