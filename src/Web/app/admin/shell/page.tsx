import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listPages } from "@/lib/experience/client";
import { ShellSettings, type AudienceOption, type ShellAdmin } from "@/features/shell-settings/ShellSettings";

/**
 * Shell settings (spec 008 FR-056, FR-058): the footer every page shows and the extra home views offered as tabs
 * after My day. The section appears only when the API lists it, and the API authorizes and validates every save;
 * audience rules are offered only when the API lets this editor read them.
 */
export default async function ShellSettingsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "shell")) {
    notFound();
  }
  const [shell, pages, rules] = await Promise.all([
    apiGet<ShellAdmin>("/api/v1/experience/shell/definition"),
    listPages(),
    apiGet<AudienceOption[]>("/api/v1/experience/audience-rules"),
  ]);
  if (!shell.ok) {
    notFound();
  }
  const t = await getTranslations("shell_settings");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <ShellSettings
        initial={shell.data}
        pages={pages.ok ? pages.data.filter((p) => p.currentRelease !== null).map((p) => ({ id: p.id, slug: p.slug })) : []}
        audiences={rules.ok ? rules.data : null}
      />
    </>
  );
}
