import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getCatalogue, listTemplates } from "@/lib/experience/client";
import { templateName } from "@/lib/experience/builder-types";
import { StateView } from "@/components/ui/StateView";
import { NewTemplateForm } from "@/features/templates/NewTemplateForm";
import "@/features/builder/builder.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Templates (spec 032 US5): the organization's own templates, each a copy of a platform template with its own names,
 * variants, region locks, versions and distribution. The section appears only when the API lists it.
 */
export default async function TemplatesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "templates")) {
    notFound();
  }
  const [templates, catalogue] = await Promise.all([listTemplates(), getCatalogue()]);
  const t = await getTranslations("templates");
  const locale = await getLocale();
  if (!templates.ok || !catalogue.ok) {
    return (
      <>
        <AdminHeader section="templates" />
        <StateView kind={templates.ok || templates.status !== 403 ? "error" : "denied"} title={t("unavailable")} message="" />
      </>
    );
  }
  const platform = catalogue.data.templates.filter((x) => !x.custom && x.state === "released");
  const platformNames = new Map(platform.map((x) => [x.id, x.name]));

  return (
    <>
      <AdminHeader section="templates" description={t("intro")} />
      {templates.data.length === 0 ? (
        <StateView kind="empty" title={t("empty")} message={t("emptyMessage")} />
      ) : (
        <div className="builder-table-wrap">
          <table className="builder-table" data-testid="templates-table">
            <thead>
              <tr>
                <th scope="col">{t("name")}</th>
                <th scope="col">{t("basedOn")}</th>
                <th scope="col">{t("version")}</th>
                <th scope="col">{t("distribution")}</th>
              </tr>
            </thead>
            <tbody>
              {templates.data.map((x) => (
                <tr key={x.id}>
                  <th scope="row">
                    <Link href={`/admin/templates/${x.id}`}>{templateName(x.latest, locale)}</Link>
                  </th>
                  <td>{platformNames.get(x.basedOn) ?? x.basedOn}</td>
                  <td>{t("versionState", { version: x.latest.version, state: x.latest.state })}</td>
                  <td>{x.distribution.length === 0 ? t("everywhere") : t("units", { count: x.distribution.length })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <NewTemplateForm bases={platform.map((x) => ({ id: x.id, name: x.name, description: x.description }))} />
    </>
  );
}
