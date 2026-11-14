import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick, type CatalogueView, type RouteActionsView, type ServiceAdminView } from "@/lib/services/types";
import type { RouteGroup } from "@/lib/services/route-graph";
import { ServiceEditor } from "@/features/services/ServiceEditor";

/** Builds one service (spec 016 US7). A service outside the owner's units is not found. */
export default async function ServiceEditorPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const [service, catalogue, units, actions, groups, all] = await Promise.all([
    apiGet<ServiceAdminView>(`/api/v1/services/admin/${encodeURIComponent(id)}`),
    apiGet<CatalogueView>("/api/v1/services"),
    listUnits(),
    apiGet<RouteActionsView>("/api/v1/services/admin/connector-actions"),
    apiGet<RouteGroup[]>("/api/v1/services/admin/groups"),
    apiGet<ServiceAdminView[]>("/api/v1/services/admin"),
  ]);
  if (!service.ok) {
    notFound();
  }
  const t = await getTranslations("services.admin");
  const locale = await getLocale();
  return (
    <>
      <p>
        <Link href="/admin/services">{t("back")}</Link>
      </p>
      <h1>
        <bdi>{pick(service.data.service.names, locale)}</bdi>
      </h1>
      <ServiceEditor
        initial={service.data}
        categories={catalogue.ok ? catalogue.data.categories : []}
        units={units.ok ? units.data : []}
        routeActions={actions.ok ? actions.data : { available: false, actions: [] }}
        groups={groups.ok ? groups.data : []}
        services={
          all.ok
            ? all.data
                .filter((v) => v.service.kind === "form")
                .map((v) => ({
                  id: v.service.id,
                  name: pick(v.service.names, locale),
                }))
            : []
        }
      />
    </>
  );
}
