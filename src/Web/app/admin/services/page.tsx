import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { CatalogueView, ServiceAdminView, ServiceTemplateView } from "@/lib/services/types";
import { ServiceAdminList } from "@/features/services/ServiceAdminList";
import { ServiceTemplateList } from "@/features/services/ServiceTemplateList";

/**
 * Services the owner manages (spec 016 US7). The section appears only when the API lists it; every call is
 * authorized again by the API.
 */
export default async function ServicesAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "services")) {
    notFound();
  }
  const [services, catalogue, units, templates] = await Promise.all([
    apiGet<ServiceAdminView[]>("/api/v1/services/admin"),
    apiGet<CatalogueView>("/api/v1/services"),
    listUnits(),
    apiGet<ServiceTemplateView[]>("/api/v1/services/admin/templates"),
  ]);
  if (!services.ok) {
    notFound();
  }
  const t = await getTranslations("services.admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <ServiceAdminList initial={services.data} categories={catalogue.ok ? catalogue.data.categories : []} units={units.ok ? units.data : []} />
      {templates.ok ? <ServiceTemplateList initial={templates.data} /> : null}
    </>
  );
}
