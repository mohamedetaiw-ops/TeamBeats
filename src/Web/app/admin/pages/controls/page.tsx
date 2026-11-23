import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getCatalogue, listUnits } from "@/lib/experience/client";
import type { AudienceRuleSummary, ComponentChoice, FeatureControl } from "@/lib/experience/builder-types";
import { StateView } from "@/components/ui/StateView";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { PagesNav } from "@/features/builder/manage/PagesNav";
import { FeatureControlsAdmin } from "@/features/builder/manage/FeatureControlsAdmin";
import "@/features/builder/builder.css";

/** Experience feature controls (spec 032 R2 T030, T032). The API decides who may switch what, and at which scope. */
export default async function FeatureControlsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const [controls, components, catalogue, units, audiences] = await Promise.all([
    apiGet<FeatureControl[]>("/api/v1/experience/feature-controls"),
    apiGet<ComponentChoice[]>("/api/v1/experience/components"),
    getCatalogue(),
    listUnits(),
    apiGet<AudienceRuleSummary[]>("/api/v1/experience/audience-rules"),
  ]);
  const t = await getTranslations("builder.manage.controls");
  return (
    <>
      <AdminHeader section="pages" description={t("intro")} />
      <PagesNav current="controls" />
      {controls.ok ? (
        <FeatureControlsAdmin
          initial={controls.data}
          components={components.ok ? components.data : []}
          templates={catalogue.ok ? catalogue.data.templates.map((x) => ({ id: x.id, name: x.name })) : []}
          units={units.ok ? units.data.filter((u) => u.status === "active") : []}
          audiences={audiences.ok ? audiences.data : []}
        />
      ) : (
        <StateView kind={controls.status === 403 ? "denied" : "error"} title={t("unavailable")} message={controls.status === 403 ? t("deniedMessage") : ""} />
      )}
    </>
  );
}
