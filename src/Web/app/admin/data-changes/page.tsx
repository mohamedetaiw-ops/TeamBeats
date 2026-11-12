import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { DataChange, DataChangeSettings } from "@/lib/data-changes";
import { DataChangeAdmin } from "@/features/data-changes/DataChangeAdmin";

/**
 * Data change settings and the changes HR covers (spec 022 FR-019). The section appears only when the API lists it;
 * when the plan lacks data changes the API refuses and the page says so.
 */
export default async function DataChangesAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "data_changes")) {
    notFound();
  }
  const [settings, changes] = await Promise.all([apiGet<DataChangeSettings>("/api/v1/hr/data-change-settings"), apiGet<DataChange[]>("/api/v1/hr/data-changes")]);
  const t = await getTranslations("data_changes.admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      {settings.ok ? <DataChangeAdmin settings={settings.data} changes={changes.ok ? changes.data : []} /> : <p className="notice">{t("notInPackage")}</p>}
    </>
  );
}
