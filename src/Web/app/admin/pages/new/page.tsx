import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getCatalogue, listUnits } from "@/lib/experience/client";
import { unitName } from "@/lib/experience/builder-types";
import { TemplateChooser } from "@/features/builder/TemplateChooser";
import "@/features/builder/builder.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * New page (spec 032 FR-017): choose a template from previews with text descriptions, name its address and scope.
 * The API decides whether the editor may create a page at the chosen scope.
 */
export default async function NewPagePage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const [catalogue, units] = await Promise.all([getCatalogue(), listUnits()]);
  if (!catalogue.ok) {
    notFound();
  }
  const t = await getTranslations("builder");
  const locale = await getLocale();
  const choices = units.ok
    ? units.data.filter((u) => u.status === "active").map((u) => ({ id: u.id, name: unitName(u, locale), level: u.level }))
    : [];
  return (
    <>
      <AdminHeader section="pages" title={t("create.title")} description={t("create.intro")} />
      <TemplateChooser
        templates={catalogue.data.templates
          .filter((x) => x.state === "released")
          // Page templates first; the employee home template last.
          .sort((a, b) => Number(a.id.startsWith("H")) - Number(b.id.startsWith("H")) || a.id.localeCompare(b.id))}
        layouts={catalogue.data.layouts}
        units={choices}
        tenantWide={nav.data.find((s) => s.key === "pages")?.wholeTenant ?? false}
      />
    </>
  );
}
