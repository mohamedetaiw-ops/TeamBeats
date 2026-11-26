import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listPages } from "@/lib/experience/client";
import { getShell } from "@/lib/shell/client";
import { ShellSettings, type AudienceOption, type ShellAdmin } from "@/features/shell-settings/ShellSettings";
import { BehaviorPolicyForm } from "@/features/shell-settings/BehaviorPolicyForm";
import type { BehaviorPolicy } from "@/lib/shell/types";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Shell settings (spec 008 FR-056, FR-058, spec 030 FR-005): the footer every page shows, the extra home views offered
 * as tabs after My day, and the bottom navigation on phones. The section appears only when the API lists it, and the API authorizes and validates every save;
 * audience rules are offered only when the API lets this editor read them.
 */
export default async function ShellSettingsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "shell")) {
    notFound();
  }
  const [shell, pages, rules, live, behavior] = await Promise.all([
    apiGet<ShellAdmin>("/api/v1/experience/shell/definition"),
    listPages(),
    apiGet<AudienceOption[]>("/api/v1/experience/audience-rules"),
    getShell(),
    apiGet<BehaviorPolicy>("/api/v1/experience/behavior-policy"),
  ]);
  if (!shell.ok) {
    notFound();
  }
  const t = await getTranslations("shell_settings");
  return (
    <>
      <AdminHeader section="shell" description={t("intro")} />
      <ShellSettings
        initial={shell.data}
        pages={pages.ok ? pages.data.filter((p) => p.currentRelease !== null).map((p) => ({ id: p.id, slug: p.slug })) : []}
        audiences={rules.ok ? rules.data : null}
        entries={live.ok ? live.data.globalMenu.map((l) => ({ nodeId: l.nodeId, label: l.label })) : []}
      />
      {behavior.ok && behavior.data.available ? <BehaviorPolicyForm initial={behavior.data} /> : null}
    </>
  );
}
