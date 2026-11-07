import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import { getCatalogue, getPage, listPickerItems, listUnits } from "@/lib/experience/client";
import { pagePath, unitName } from "@/lib/experience/builder-types";
import { LayoutPreview } from "@/features/builder/LayoutPreview";
import { EditorMount } from "@/features/builder/editor/EditorMount";
import "@/features/builder/builder.css";

/**
 * One page in the builder (spec 032 US1): its template and scope, then the editor. A page outside the editor's scope is
 * not found, as the API answers it.
 */
export default async function BuilderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const [page, catalogue, units, items, me] = await Promise.all([getPage(id), getCatalogue(), listUnits(), listPickerItems(id), apiGet<Me>("/api/v1/me")]);
  if (!page.ok || !catalogue.ok || !items.ok || !me.ok) {
    notFound();
  }
  const t = await getTranslations("builder");
  const locale = await getLocale();
  const template = catalogue.data.templates.find((x) => x.id === page.data.templateId);
  const layout = catalogue.data.layouts.find((l) => l.id === page.data.draft.layout.id);
  const unit = page.data.orgUnitId && units.ok ? units.data.find((u) => u.id === page.data.orgUnitId) : undefined;
  const regionName = (r: string) => t(`regions.${r}`);

  return (
    <>
      <div className="builder-heading">
        <h1>
          <bdi dir="ltr">{pagePath(page.data.slug)}</bdi>
        </h1>
        {page.data.currentRelease !== null ? (
          <Link className="button button-secondary" href={pagePath(page.data.slug)}>
            {t("page.open")}
          </Link>
        ) : null}
      </div>
      <dl className="builder-facts">
        <dt>{t("pages.template")}</dt>
        <dd>{template?.name ?? page.data.templateId}</dd>
        <dt>{t("pages.scope")}</dt>
        <dd>{page.data.orgUnitId ? (unit ? unitName(unit, locale) : t("pages.unit")) : t("pages.wholeTenant")}</dd>
      </dl>
      {layout ? <LayoutPreview layout={layout} regionName={regionName} /> : null}
      <EditorMount
        page={page.data}
        template={template}
        tenantId={me.data.tenant.id}
        locales={me.data.tenant.enabledLocales}
        catalogueItems={items.data}
      />
    </>
  );
}
