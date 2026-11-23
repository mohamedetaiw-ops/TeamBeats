import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { getCatalogue, listPages, listUnits } from "@/lib/experience/client";
import type { Fragment } from "@/lib/experience/builder-types";
import { StateView } from "@/components/ui/StateView";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { PagesNav } from "@/features/builder/manage/PagesNav";
import { FragmentsAdmin } from "@/features/builder/manage/FragmentsAdmin";
import "@/features/builder/builder.css";

/** Fragments (spec 032 R2 T027, T032): reusable groups of placements. Every read and change is authorized by the API. */
export default async function FragmentsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "pages")) {
    notFound();
  }
  const [fragments, units, pages, catalogue] = await Promise.all([
    apiGet<Fragment[]>("/api/v1/experience/fragments"),
    listUnits(),
    listPages(),
    getCatalogue(),
  ]);
  const t = await getTranslations("builder.manage.fragments");
  return (
    <>
      <AdminHeader section="pages" description={t("intro")} />
      <PagesNav current="fragments" />
      {fragments.ok ? (
        <FragmentsAdmin
          initial={fragments.data}
          units={units.ok ? units.data.filter((u) => u.status === "active") : []}
          pages={pages.ok ? pages.data : []}
          templates={catalogue.ok ? catalogue.data.templates : []}
        />
      ) : (
        <StateView kind={fragments.status === 403 ? "denied" : "error"} title={t("unavailable")} message={fragments.status === 403 ? t("deniedMessage") : ""} />
      )}
    </>
  );
}
