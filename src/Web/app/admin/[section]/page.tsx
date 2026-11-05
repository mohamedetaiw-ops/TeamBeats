import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";

export default async function AdminSection({ params }: { params: Promise<{ section: string }> }) {
  const { section } = await params;
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  const entry = nav.ok ? nav.data.find((s) => s.path === `/admin/${section}`) : undefined;
  if (!entry) {
    notFound();
  }
  const t = await getTranslations();
  return (
    <>
      <h1>{t(entry.labelKey)}</h1>
      <p>{t(entry.wholeTenant ? "admin.wholeTenant" : "admin.scoped")}</p>
      <p>{t("admin.sectionPending")}</p>
    </>
  );
}
