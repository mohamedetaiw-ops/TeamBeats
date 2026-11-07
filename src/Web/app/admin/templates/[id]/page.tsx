import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getCatalogue, getTemplate, listUnits } from "@/lib/experience/client";
import { templateName, unitName } from "@/lib/experience/builder-types";
import { LayoutPreview } from "@/features/builder/LayoutPreview";
import { TemplateDesigner } from "@/features/templates/TemplateDesigner";
import "@/features/builder/builder.css";

/**
 * One tenant template (spec 032 US5): names, variants, region policies and locks, versions and distribution. Another
 * organization's template, or one that does not exist, is not found, as the API answers it.
 */
export default async function TemplatePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "templates")) {
    notFound();
  }
  const [template, catalogue, units] = await Promise.all([getTemplate(id), getCatalogue(), listUnits()]);
  if (!template.ok || !catalogue.ok) {
    notFound();
  }
  const t = await getTranslations("templates");
  const tb = await getTranslations("builder");
  const locale = await getLocale();
  const base = catalogue.data.templates.find((x) => x.id === template.data.basedOn && !x.custom);
  const layout = catalogue.data.layouts.find((l) => l.id === template.data.latest.layout);
  const choices = units.ok
    ? units.data.filter((u) => u.status === "active").map((u) => ({ id: u.id, name: unitName(u, locale), level: u.level }))
    : [];

  return (
    <>
      <div className="builder-heading">
        <h1>{templateName(template.data.latest, locale)}</h1>
        <Link className="button button-secondary" href="/admin/templates">
          {t("back")}
        </Link>
      </div>
      <dl className="builder-facts">
        <dt>{t("id")}</dt>
        <dd>
          <bdi dir="ltr">{template.data.id}</bdi>
        </dd>
        <dt>{t("basedOn")}</dt>
        <dd>{base?.name ?? template.data.basedOn}</dd>
      </dl>
      {layout ? <LayoutPreview layout={layout} regionName={(r) => tb(`regions.${r}`)} /> : null}
      <TemplateDesigner template={template.data} baseVariants={base?.variants ?? template.data.latest.variants} units={choices} />
    </>
  );
}
