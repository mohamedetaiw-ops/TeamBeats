import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { PageHeader } from "@/components/ui/PageHeader";
import { HelpEditor, type HelpAdmin } from "@/features/shell-settings/HelpEditor";

/**
 * Help and tours (spec 008 FR-020, T024): hints and short tours for addresses of the intranet, published as versions.
 * The section appears only when the API lists it, and the API authorizes and validates every publish.
 */
export default async function HelpSettingsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "help")) {
    notFound();
  }
  const help = await apiGet<HelpAdmin>("/api/v1/experience/help");
  if (!help.ok) {
    notFound();
  }
  const t = await getTranslations("help_settings");
  return (
    <>
      <PageHeader title={t("title")} description={t("intro")} />
      <HelpEditor initial={help.data} />
    </>
  );
}
