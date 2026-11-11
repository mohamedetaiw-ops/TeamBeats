import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { OrgUnitView, UserDetail } from "@/lib/admin/types";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { UserDetailAdmin } from "@/features/admin/UserDetailAdmin";
import { roleNames } from "@/features/admin/roleNames";

/**
 * One person in administration (spec 006 FR-009). Someone outside the administrator's scope, or in another workspace,
 * is not found, exactly as the API answers.
 */
export default async function UserPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "users")) {
    notFound();
  }
  const [detail, units] = await Promise.all([apiGet<UserDetail>(`/api/v1/admin/users/${encodeURIComponent(id)}`), apiGet<OrgUnitView[]>("/api/v1/org/units")]);
  if (!detail.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <Breadcrumb label={t("users.breadcrumb")} crumbs={[{ label: t("nav.users"), href: "/admin/users" }, { label: detail.data.user.displayName, href: `/admin/users/${detail.data.user.id}` }]} />
      <UserDetailAdmin initial={detail.data} units={units.ok ? units.data : []} roleName={await roleNames()} />
    </>
  );
}
