import { notFound } from "next/navigation";
import { getMessages, getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import type { DirectoryView, IdentityProvider, OrgUnitView, RoleView } from "@/lib/admin/types";
import { PageHeader } from "@/components/ui/PageHeader";
import { IdentityAdmin } from "@/features/admin/IdentityAdmin";

/**
 * Sign-in settings (spec 003 US2 and US6). The section appears only when the API lists it; saving is authorized again
 * by the API, which also checks the plan and asks for a fresh sign-in.
 */
export default async function IdentityPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "identity")) {
    notFound();
  }
  const [me, provider, directory, roles, units] = await Promise.all([
    apiGet<Me>("/api/v1/me"),
    apiGet<IdentityProvider | null>("/api/v1/tenant/identity-provider"),
    apiGet<DirectoryView>("/api/v1/tenant/directory"),
    apiGet<RoleView[]>("/api/v1/authz/roles"),
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
  ]);
  if (!me.ok || !directory.ok) {
    notFound();
  }
  const messages = (await getMessages()) as { admin?: { roles?: { names?: Record<string, string> } } };
  const names = messages.admin?.roles?.names ?? {};
  // Entra groups map to built-in roles only (spec 003 US2 scenario 6).
  const builtIn = roles.ok ? roles.data.filter((r) => r.isBuiltIn) : [];
  const t = await getTranslations("admin");
  return (
    <>
      <PageHeader title={t("nav.identity")} description={t("identity.description")} icon="lock" />
      <IdentityAdmin
        tenantId={me.data.tenant.id}
        initialProvider={provider.ok ? provider.data : null}
        initialDirectory={directory.data}
        roles={builtIn.map((r) => ({ key: r.key, name: names[r.key] ?? r.key }))}
        units={units.ok ? units.data : []}
      />
    </>
  );
}
