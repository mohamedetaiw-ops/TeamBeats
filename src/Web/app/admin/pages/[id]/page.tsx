import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getCatalogue, getPage, listUnits } from "@/lib/experience/client";
import { pagePath, unitName } from "@/lib/experience/builder-types";
import { LayoutPreview } from "@/features/builder/LayoutPreview";
import "@/features/builder/builder.css";

/**
 * One page in the builder (spec 032 US1): its template, scope, release and regions. A page outside the editor's scope
 * is not found, as the API answers it.
 */
export default async function BuilderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const [page, catalogue, units] = await Promise.all([getPage(id), getCatalogue(), listUnits()]);
  if (!page.ok || !catalogue.ok) {
    notFound();
  }
  const t = await getTranslations("builder");
  const locale = await getLocale();
  const template = catalogue.data.templates.find((x) => x.id === page.data.templateId);
  const layout = catalogue.data.layouts.find((l) => l.id === page.data.draft.layout.id);
  const unit = page.data.orgUnitId && units.ok ? units.data.find((u) => u.id === page.data.orgUnitId) : undefined;
  const blocking = page.data.findings.filter((f) => f.severity === "blocking").length;
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
        <dt>{t("pages.status")}</dt>
        <dd>{page.data.currentRelease === null ? t("pages.draftOnly") : t("pages.published", { release: page.data.currentRelease })}</dd>
        <dt>{t("page.findings")}</dt>
        <dd>{blocking > 0 ? t("page.blocking", { count: blocking }) : t("page.noBlocking")}</dd>
      </dl>
      {layout ? <LayoutPreview layout={layout} regionName={regionName} /> : null}
      <h2>{t("page.regions")}</h2>
      <ul className="builder-regions">
        {page.data.draft.regions.map((r) => (
          <li key={r.id} className="builder-region">
            <strong>{regionName(r.id)}</strong> {t("page.placements", { count: r.placements.length })}
          </li>
        ))}
      </ul>
      <p className="notice">{t("page.editorPending")}</p>
    </>
  );
}
