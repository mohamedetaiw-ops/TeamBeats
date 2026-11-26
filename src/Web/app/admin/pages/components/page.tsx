import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { TenantComponents as View } from "@/lib/experience/builder-types";
import { StateView } from "@/components/ui/StateView";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { PagesNav } from "@/features/builder/manage/PagesNav";
import { TenantComponents } from "@/features/builder/manage/TenantComponents";
import "@/features/builder/builder.css";

/** Components the organization builds itself (spec 032 R3 FR-031): submit for review, try in the sandbox, turn on. */
export default async function TenantComponentsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const view = await apiGet<View>("/api/v1/experience/tenant-components");
  const t = await getTranslations("builder.manage.tenantComponents");
  return (
    <>
      <AdminHeader section="pages" description={t("intro")} />
      <PagesNav current="components" />
      {view.ok ? (
        <TenantComponents initial={view.data} />
      ) : (
        <StateView kind={view.status === 403 ? "denied" : "error"} title={t("unavailable")} message={view.status === 403 ? t("deniedMessage") : ""} />
      )}
    </>
  );
}
