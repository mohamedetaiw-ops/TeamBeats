import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { UserSummary } from "@/lib/admin/types";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { UsersAdmin } from "@/features/admin/UsersAdmin";

/**
 * People in administration (spec 006 FR-009). The section appears only when the API lists it, and the list holds only
 * the people the API returns for the administrator's scope.
 */
export default async function UsersPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  const section = nav.ok ? nav.data.find((s) => s.key === "users") : undefined;
  if (!section) {
    notFound();
  }
  const users = await apiGet<UserSummary[]>("/api/v1/admin/users");
  if (!users.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <AdminHeader section="users" description={t(section.wholeTenant ? "users.description" : "users.descriptionScoped")} />
      <UsersAdmin initial={users.data} />
    </>
  );
}
