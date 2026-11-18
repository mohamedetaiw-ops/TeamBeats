import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AssignmentView, OrgUnitView, RoleView } from "@/lib/admin/types";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { RolesAdmin } from "@/features/admin/RolesAdmin";
import { roleNames } from "@/features/admin/roleNames";

/**
 * Roles and access (spec 004 US1 to US6). The section appears only when the API lists it; assignments are those the
 * API returns for the administrator's scope, and every change is authorized again by the API.
 */
export default async function RolesPage({ searchParams }: { searchParams: Promise<{ user?: string }> }) {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "roles")) {
    notFound();
  }
  const [roles, assignments, units] = await Promise.all([
    apiGet<RoleView[]>("/api/v1/authz/roles"),
    apiGet<AssignmentView[]>("/api/v1/authz/assignments"),
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
  ]);
  if (!roles.ok) {
    notFound();
  }
  const { user } = await searchParams;
  const focus = user && /^[0-9a-f-]{36}$/i.test(user) ? user : null;
  const t = await getTranslations("admin");
  return (
    <>
      <AdminHeader section="roles" description={t("roles.description")} />
      <RolesAdmin
        initialRoles={roles.data}
        initialAssignments={assignments.ok ? assignments.data : null}
        units={units.ok ? units.data : []}
        roleName={await roleNames(roles.data)}
        focusUser={focus}
      />
    </>
  );
}
