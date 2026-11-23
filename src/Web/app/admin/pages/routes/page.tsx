import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { PageRoute } from "@/lib/experience/builder-types";
import { StateView } from "@/components/ui/StateView";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { PagesNav } from "@/features/builder/manage/PagesNav";
import { ApprovalRoutes } from "@/features/builder/manage/ApprovalRoutes";
import "@/features/builder/builder.css";

/**
 * Page approval routes (spec 032 R2 T023, T032). The section appears only when the API lists it; the routes, the scopes and
 * every change are the API's, and a package without approval routes shows the denied state.
 */
export default async function ApprovalRoutesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const [routes, units] = await Promise.all([apiGet<PageRoute[]>("/api/v1/experience/page-routes"), listUnits()]);
  const t = await getTranslations("builder.manage.routes");
  return (
    <>
      <AdminHeader section="pages" description={t("intro")} />
      <PagesNav current="routes" />
      {routes.ok ? (
        <ApprovalRoutes initial={routes.data} units={units.ok ? units.data.filter((u) => u.status === "active") : []} />
      ) : (
        <StateView kind={routes.status === 403 ? "denied" : "error"} title={t("unavailable")} message={routes.status === 403 ? t("deniedMessage") : ""} />
      )}
    </>
  );
}
