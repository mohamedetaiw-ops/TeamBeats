import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AdminConsent, ApiApp, ApiScope, ApiSettings } from "@/lib/api-access";
import { ApiAccessAdmin } from "@/features/api-access/ApiAccessAdmin";

/**
 * API and MCP access (spec 035 US1, US6, FR-029). The section appears only when the API lists it, for API
 * administrators and viewers; every call is authorized again by the API.
 */
export default async function ApiAccessPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "api")) {
    notFound();
  }
  const [apps, scopes, consents, settings] = await Promise.all([
    apiGet<ApiApp[]>("/api/v1/api-access/apps"),
    apiGet<ApiScope[]>("/api/v1/api-access/scopes"),
    apiGet<AdminConsent[]>("/api/v1/api-access/consents"),
    apiGet<ApiSettings>("/api/v1/api-access/settings"),
  ]);
  if (!apps.ok || !scopes.ok || !consents.ok || !settings.ok) {
    notFound();
  }
  const t = await getTranslations("apiAccess.admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <ApiAccessAdmin apps={apps.data} scopes={scopes.data} consents={consents.data} settings={settings.data} />
    </>
  );
}
