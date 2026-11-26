import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { Location, Policy } from "@/lib/people/types";
import { PeoplePolicyAdmin } from "@/features/people/PeoplePolicyAdmin";
import { SkillListAdmin } from "@/features/people/SkillAdmin";
import type { SkillListView } from "@/lib/people/skills";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * People directory administration (spec 010 US1): field sources and visibility, tenant fields, the new joiner period,
 * the location attribute and location names, and the skill list (R2 US6). The section appears only when the API lists it.
 */
export default async function PeopleAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "people")) {
    notFound();
  }
  const [policy, locations, skills] = await Promise.all([
    apiGet<Policy>("/api/v1/people/policy"),
    apiGet<Location[]>("/api/v1/people/locations"),
    apiGet<SkillListView>("/api/v1/people/skills"),
  ]);
  if (!policy.ok) {
    notFound();
  }
  const t = await getTranslations("people.admin");
  return (
    <>
      <AdminHeader section="people" description={t("intro")} />
      <PeoplePolicyAdmin initialPolicy={policy.data} initialLocations={locations.ok ? locations.data : []} />
      {skills.ok && skills.data.canManage ? <SkillListAdmin initial={skills.data} /> : null}
    </>
  );
}
