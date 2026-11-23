import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { ComponentDeployments } from "@/lib/experience/builder-types";
import { StateView } from "@/components/ui/StateView";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { PagesNav } from "@/features/builder/manage/PagesNav";
import { ComponentUpgrades } from "@/features/builder/manage/ComponentUpgrades";
import "@/features/builder/builder.css";

/** Component upgrades (spec 032 R2 T031, T032): deploy a component's next major to live pages, or roll it back. */
export default async function ComponentUpgradesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const view = await apiGet<ComponentDeployments>("/api/v1/experience/components/deployments");
  const t = await getTranslations("builder.manage.upgrades");
  return (
    <>
      <AdminHeader section="pages" description={t("intro")} />
      <PagesNav current="upgrades" />
      {view.ok ? (
        <ComponentUpgrades initial={view.data} />
      ) : (
        <StateView kind={view.status === 403 ? "denied" : "error"} title={t("unavailable")} message={view.status === 403 ? t("deniedMessage") : ""} />
      )}
    </>
  );
}
